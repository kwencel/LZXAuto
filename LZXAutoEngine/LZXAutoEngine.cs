﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace LZXAutoEngine
{
	public class LZXAutoEngine
	{

		private ConcurrentDictionary<int, uint> fileDict = new ConcurrentDictionary<int, uint>();

		//private const int fileSaveTimerMs = (int)30e3; //30 seconds
		private const int treadPoolWaitMs = 200;
		private const string dbFileName = "FileDict.db";

		private uint fileCountProcessedByCompactCommand = 0;
		private uint fileCountSkipByNoChange = 0;
		private uint fileCountSkippedByAttributes = 0;
		private uint fileCountSkippedByExtension = 0;
		private uint dictEntriesCount0 = 0;
		private int threadQueueLength;

		private ulong compactCommandBytesRead = 0;
		private ulong compactCommandBytesWritten = 0;

		private ulong totalDiskBytesLogical = 0;
		private ulong totalDiskBytesPhysical = 0;

		private string[] skipFileExtensions;

		private readonly CancellationTokenSource cancelToken = new CancellationTokenSource();
		private readonly int maxQueueLength = Environment.ProcessorCount * 16;

		public Logger Logger { get; set; } = new Logger(LogLevel.General);

		public bool IsElevated
		{
			get
			{
				using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
				{
					WindowsPrincipal principal = new WindowsPrincipal(identity);
					return principal.IsInRole(WindowsBuiltInRole.Administrator);
				}
			}
		}

		public LZXAutoEngine()
		{
			//timer = new Timer(FileSaveTimerCallback, null, fileSaveTimerMs, fileSaveTimerMs);
		}

		public void Process(string path, string[] skipFileExtensionsArr)
		{
			skipFileExtensions = skipFileExtensionsArr ?? new string[] { };

			Logger.Log($"Starting new compressing session. LZXAuto version: {Assembly.GetEntryAssembly().GetName().Version}", 20);
			Logger.Log($"Running in Administrator mode: {IsElevated}", 2);
			Logger.Log($"Starting path {path}", 2);

			DateTime startTimeStamp = DateTime.Now;

			try
			{
				fileDict = LoadDictFromFile(dbFileName);

				DirectoryInfo dirTop = new DirectoryInfo(path);

				foreach (var fi in dirTop.EnumerateFiles())
				{
					if (cancelToken.IsCancellationRequested)
					{
						// wait until all threads complete
						FinalizeThreadPool();
						break;
					}

					try
					{
						Interlocked.Increment(ref threadQueueLength);

						ThreadPool.QueueUserWorkItem(a =>
						{
							ProcessFile(fi);
						});

						// Do not let queue length more items than MaxQueueLength
						while (threadQueueLength > maxQueueLength)
						{
							Thread.Sleep(treadPoolWaitMs);
						}
					}
					catch (Exception ex)
					{
						Logger.Log(ex, fi);
					}
				}

				foreach (var di in dirTop.EnumerateDirectories("*", new EnumerationOptions() { RecurseSubdirectories = true, ReturnSpecialDirectories = false }))
				{
					try
					{
						foreach (var fi in di.EnumerateFiles("*", new EnumerationOptions() { AttributesToSkip = FileAttributes.Directory }))
						{
							if (cancelToken.IsCancellationRequested)
							{
								// wait until all threads complete
								FinalizeThreadPool();
								break;
							}

							try
							{
								Interlocked.Increment(ref threadQueueLength);

								ThreadPool.QueueUserWorkItem(a =>
								{
									ProcessFile(fi);
								});


								// Do not let queue length more items than MaxQueueLength
								while (threadQueueLength > maxQueueLength)
								{
									Thread.Sleep(treadPoolWaitMs);
								}
							}
							catch (Exception ex)
							{
								Logger.Log(ex, fi);
							}
						}

						DirectoryRemoveCompressAttr(di);
					}
					catch (UnauthorizedAccessException)
					{
						Logger.Log($"Access failed to folder: {di.FullName}", 2, LogLevel.General);
					}
					catch (Exception ex)
					{
						Logger.Log(ex, di);
					}
				}

				DirectoryRemoveCompressAttr(dirTop);
			}
			catch (DirectoryNotFoundException DirNotFound)
			{
				Logger.Log(DirNotFound.Message);
			}
			catch (UnauthorizedAccessException unAuth)
			{
				Logger.Log(unAuth, unAuth.Message);
			}
			catch (PathTooLongException LongPath)
			{
				Logger.Log(LongPath.Message);
			}
			catch (Exception ex)
			{
				Logger.Log($"Other error: {ex.Message}");
			}
			finally
			{
				// Wait until all threads complete
				FinalizeThreadPool();

				Logger.Log("Completed");

				SaveDictToFile(dbFileName, fileDict);

				TimeSpan ts = DateTime.Now.Subtract(startTimeStamp);
				uint totalFilesVisited = fileCountProcessedByCompactCommand + fileCountSkipByNoChange + fileCountSkippedByAttributes + fileCountSkippedByExtension;

				StringBuilder statStr = new StringBuilder();

				string spaceSavings = "-";
				if (compactCommandBytesRead > 0) spaceSavings = $"{ (1 - (decimal)compactCommandBytesWritten / compactCommandBytesRead) * 100m:0.00}%";

				string compressionRatio = "-";
				if (compactCommandBytesWritten > 0) compressionRatio = $"{(decimal)compactCommandBytesRead / compactCommandBytesWritten:0.00}";

				Logger.Log(
					$"Stats for this session: {Environment.NewLine}" +
					$"Files skipped by attributes: {fileCountSkippedByAttributes}{Environment.NewLine}" +
					$"Files skipped by extension: { fileCountSkippedByExtension}{Environment.NewLine}" +
					$"Files skipped by no change: { fileCountSkipByNoChange}{Environment.NewLine}" +
					$"Files processed by compact command line: { fileCountProcessedByCompactCommand}{Environment.NewLine}" +
					$"Files in db: {fileDict?.Count ?? 0}{Environment.NewLine}" +
					$"Files in db delta: {(fileDict?.Count ?? 0) - dictEntriesCount0}{Environment.NewLine}" +
					$"Files visited: {totalFilesVisited}{Environment.NewLine}" +
					$"{Environment.NewLine}" +

					$"Bytes read: {compactCommandBytesRead.GetMemoryString()}{Environment.NewLine}" +
					$"Bytes written: {compactCommandBytesWritten.GetMemoryString()}{Environment.NewLine}" +
					$"Space savings bytes: {(compactCommandBytesRead - compactCommandBytesWritten).GetMemoryString()}{Environment.NewLine}" +
					$"Space savings: {spaceSavings}{Environment.NewLine}" +
					$"Compression ratio: { compressionRatio }{Environment.NewLine}{Environment.NewLine}" +

					$"Disk stat:{Environment.NewLine}" +
					$"Files logical size: {totalDiskBytesLogical.GetMemoryString()}{Environment.NewLine}" +
					$"Files phisical size: {totalDiskBytesPhysical.GetMemoryString()}{Environment.NewLine}" +
					$"Space savings: {(1 - (decimal)totalDiskBytesPhysical / totalDiskBytesLogical) * 100m:0.00}%{Environment.NewLine}" +
					$"Compression ratio: {(decimal)totalDiskBytesLogical / totalDiskBytesPhysical:0.00}"
					, 2, LogLevel.General);

				Logger.Log(
					$"Perf stats:{Environment.NewLine}" +
					$"Time elapsed[hh:mm:ss:ms]: {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}:{ts.Milliseconds:00}{Environment.NewLine}" +
					$"Compressed files per minute: {fileCountProcessedByCompactCommand / ts.TotalMinutes:0.00}{Environment.NewLine}" +
					$"Files per minute: {totalFilesVisited / ts.TotalMinutes:0.00}", 2, LogLevel.General, false);
			}
		}


		private void ProcessFile(FileInfo fi)
		{
			try
			{
				ulong physicalSize1_Clusters = DriveUtils.GetPhysicalFileSize(fi.FullName);
				ulong logicalSize_Clusters = DriveUtils.GetDiskOccupiedSpace((ulong)fi.Length, fi.FullName);

				ThreadUtils.InterlockedAdd(ref totalDiskBytesLogical, logicalSize_Clusters);

				if (skipFileExtensions.Any(c => c == fi.Extension))
				{
					ThreadUtils.InterlockedIncrement(ref fileCountSkippedByExtension);
					ThreadUtils.InterlockedAdd(ref totalDiskBytesPhysical, physicalSize1_Clusters);
					return;
				}

				if (fi.Attributes.HasFlag(FileAttributes.System))
				{
					ThreadUtils.InterlockedIncrement(ref fileCountSkippedByAttributes);
					ThreadUtils.InterlockedAdd(ref totalDiskBytesPhysical, physicalSize1_Clusters);
					return;
				}

				bool useForceCompress = false;
				if (fi.Attributes.HasFlag(FileAttributes.Compressed))
				{
					File.SetAttributes(fi.FullName, fi.Attributes & ~FileAttributes.Compressed);
					useForceCompress = true;
				}

				if (fi.Length > 0)
				{
					Logger.Log("", 4, LogLevel.Debug);


					int filePathHash = fi.FullName.GetDeterministicHashCode();

					if (fileDict.TryGetValue(filePathHash, out uint dictFileSize) && dictFileSize == fi.Length)
					{
						Logger.Log($"Skipping file: '{fi.FullName}' because it has been visited already and its size ('{fi.Length.GetMemoryString()}') did not change", 1, LogLevel.Debug);
						ThreadUtils.InterlockedIncrement(ref fileCountSkipByNoChange);
						ThreadUtils.InterlockedAdd(ref totalDiskBytesPhysical, physicalSize1_Clusters);
						return;
					}

					Logger.Log($"Compressing file {fi.FullName}", 1, LogLevel.Debug);
					ThreadUtils.InterlockedIncrement(ref fileCountProcessedByCompactCommand);

					string outPut = CompactCommand($"/c /exe:LZX {(useForceCompress ? "/f" : "")} \"{fi.FullName}\"");

					ulong physicalSize2_Clusters = DriveUtils.GetPhysicalFileSize(fi.FullName);
					fileDict[filePathHash] = unchecked((uint)fi.Length);

					if (physicalSize2_Clusters > physicalSize1_Clusters)
						Logger.Log($"fileDiskSize2: {physicalSize2_Clusters} > fileDiskSize1 {physicalSize1_Clusters}, fileName: {fi.FullName}", 1, LogLevel.General);

					ThreadUtils.InterlockedAdd(ref compactCommandBytesRead, physicalSize1_Clusters);
					ThreadUtils.InterlockedAdd(ref compactCommandBytesWritten, physicalSize2_Clusters);
					ThreadUtils.InterlockedAdd(ref totalDiskBytesPhysical, physicalSize2_Clusters);

					Logger.Log(outPut, 2, LogLevel.Debug);
				}
			}
			catch (Exception ex)
			{
				Logger.Log(ex, fi);
			}
			finally
			{
				Interlocked.Decrement(ref threadQueueLength);
			}
		}

		private void DirectoryRemoveCompressAttr(DirectoryInfo dirTop)
		{
			if (dirTop.Attributes.HasFlag(FileAttributes.Compressed))
			{
				Logger.Log($"Removing NTFS compress flag on folder {dirTop.FullName} in favor of LZX compression", 1, LogLevel.General);

				string outPut = CompactCommand($"/u \"{dirTop.FullName}\"");

				Logger.Log(outPut, 2, LogLevel.Debug);
			}
		}

		private string CompactCommand(string arguments)
		{
			var proc = new Process();
			proc.StartInfo.FileName = $"compact";
			proc.StartInfo.Arguments = arguments;
			proc.StartInfo.UseShellExecute = false;
			proc.StartInfo.RedirectStandardOutput = true;

			Logger.Log(arguments, 1, LogLevel.Debug, true);

			proc.Start();
			try
			{
				proc.PriorityClass = ProcessPriorityClass.Idle;
			}
			catch (InvalidOperationException)
			{
				Logger.Log("Process Compact exited before setting its priority. Nothing to worry about.", 3, LogLevel.Debug);
			}

			string outPut = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit();
			proc.Close();

			return outPut;
		}

		public void ResetDb()
		{
			File.Delete(dbFileName);
		}

		public void SaveDictToFile(string fileName, ConcurrentDictionary<int, uint> concDict)
		{
			try
			{
				lock (ThreadUtils.lockObject)
				{
					BinaryFormatter binaryFormatter = new BinaryFormatter();
					using (FileStream writerFileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write))
					{
						Logger.Log("Saving file...", 1, LogLevel.Debug);

						var dict = concDict.ToDictionary(a => a.Key, b => b.Value);
						binaryFormatter.Serialize(writerFileStream, dict);

						Logger.Log($"File saved, dictCount: {concDict.Count}, fileSize: {writerFileStream.Length}", 1, LogLevel.Debug);

						writerFileStream.Close();
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log($"Unable to save dic to file, {ex.Message}");
			}
		}

		public ConcurrentDictionary<int, uint> LoadDictFromFile(string fileName)
		{
			ConcurrentDictionary<int, uint> retVal = new ConcurrentDictionary<int, uint>();

			if (File.Exists(fileName))
			{
				try
				{
					Logger.Log("Dictionary file found");

					BinaryFormatter binaryFormatter = new BinaryFormatter();
					using (FileStream readerFileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read))
					{
						if (readerFileStream.Length > 0)
						{
							var dict = binaryFormatter.Deserialize(readerFileStream);
							retVal = new ConcurrentDictionary<int, uint>((Dictionary<int, uint>)dict);

							readerFileStream.Close();
						}
					}

					dictEntriesCount0 = (uint)(retVal?.Count ?? 0);

					Logger.Log($"Loaded from file ({dictEntriesCount0} entries)");
				}
				catch (Exception ex)
				{
					Logger.Log($"Error during loading from file: {ex.Message}" +
						$"{Environment.NewLine}Terminating.");

					Environment.Exit(-1);
				}
			}
			else
			{
				Logger.Log("DB file not found");
			}

			return retVal;
		}

		//private void FileSaveTimerCallback(object state)
		//{
		//    Logger.Log("Saving dictionary file...", 1, LogFlags.Debug);
		//    SaveDictToFile();
		//}

		public void Cancel()
		{
			Logger.Log("Terminating...", 3, LogLevel.General);
			cancelToken.Cancel();
		}

		private void FinalizeThreadPool()
		{
			// Disable file save timer callback
			//timer.Change(Timeout.Infinite, Timeout.Infinite);

			// Wait for thread pool to complete
			while (threadQueueLength > 0)
			{
				Thread.Sleep(treadPoolWaitMs);
			}
		}
	}
}
