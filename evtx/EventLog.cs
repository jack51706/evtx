﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Alphaleonis.Win32.Filesystem;
using FluentValidation.Results;
using Force.Crc32;
using NLog;
using ServiceStack;
using ServiceStack.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;

//TODO rename project to EventLog?
namespace evtx
{
    public class EventLog
    {
        [Flags]
        public enum EventLogFlag
        {
            None = 0x0,
            IsDirty = 0x1,
            IsFull = 0x2
        }

        private static readonly Logger Logger = LogManager.GetLogger("EventLog");

        public Dictionary<long, int> EventIdMetrics;

        public EventLog(Stream fileStream)
        {
            const long eventSignature = 0x00656c6946666c45;
            const long chunkSignature = 0x6B6E6843666C45;

            var headerBytes = new byte[4096];
            fileStream.Read(headerBytes, 0, 4096);

            if (BitConverter.ToInt64(headerBytes, 0) != eventSignature)
            {
                throw new Exception("Invalid signature! Expected 'ElfFile'");
            }

            FirstChunkNumber = BitConverter.ToInt64(headerBytes, 0x8);
            LastChunkNumber = BitConverter.ToInt64(headerBytes, 0x10);

            NextRecordId = BitConverter.ToInt64(headerBytes, 0x18);
            var unusedSize = BitConverter.ToInt32(headerBytes, 0x20);

            MinorVersion = BitConverter.ToInt16(headerBytes, 0x24);
            MajorVersion = BitConverter.ToInt16(headerBytes, 0x26);

            var unusedHeaderSize = BitConverter.ToInt16(headerBytes, 0x28);
            ChunkCount = BitConverter.ToInt16(headerBytes, 0x2A);

            Flags = (EventLogFlag) BitConverter.ToInt32(headerBytes, 0x78);

            Crc = BitConverter.ToInt32(headerBytes, 0x7C);

            var inputArray = new byte[120 + 4];
            Buffer.BlockCopy(headerBytes, 0, inputArray, 0, 120);

            Crc32Algorithm.ComputeAndWriteToEnd(inputArray); // last 4 bytes contains CRC
            CalculatedCrc = BitConverter.ToInt32(inputArray, inputArray.Length - 4);

            //we are at offset 0x1000 and ready to start

            //chunk size == 65536, or 0x10000

            var chunkBuffer = new byte[0x10000];

            Chunks = new List<ChunkInfo>();

            var chunkOffset = fileStream.Position;
            var bytesRead = fileStream.Read(chunkBuffer, 0, 0x10000);

            EventIdMetrics = new Dictionary<long, int>();

            var chunkNumber = 0;
            while (bytesRead > 0)
            {
                var chunkSig = BitConverter.ToInt64(chunkBuffer, 0);

                Logger.Trace(
                    $"chunk offset: 0x{chunkOffset:X}, sig: {Encoding.ASCII.GetString(chunkBuffer, 0, 8)} signature val: 0x{chunkSig:X}");

                if (chunkSig == chunkSignature)
                {
                    var ci = new ChunkInfo(chunkBuffer, chunkOffset, chunkNumber);
                    Chunks.Add(ci);
                    TotalEventLogs += ci.EventRecords.Count;
                }
                else
                {
                    Logger.Trace($"Skipping chunk at 0x{chunkOffset:X} as it does not have correct signature");
                }

                chunkOffset = fileStream.Position;
                bytesRead = fileStream.Read(chunkBuffer, 0, 0x10000);

                chunkNumber += 1;
            }

          

            ErrorRecords = new Dictionary<long, string>();

            foreach (var chunkInfo in Chunks)
            {
                foreach (var eventIdMetric in chunkInfo.EventIdMetrics)
                {
                    if (EventIdMetrics.ContainsKey(eventIdMetric.Key) == false)
                    {
                        EventIdMetrics.Add(eventIdMetric.Key, 0);
                    }

                    EventIdMetrics[eventIdMetric.Key] += eventIdMetric.Value;
                }

                foreach (var chunkInfoErrorRecord in chunkInfo.ErrorRecords)
                {
                    ErrorRecords.Add(chunkInfoErrorRecord.Key, chunkInfoErrorRecord.Value);
                }
            }
        }

        public static Dictionary<int,EventLogMap> EventLogMaps { get; private set; }

        public static void LoadMaps(string mapPath)
        {
            EventLogMaps = new Dictionary<int, EventLogMap>();

            var f = new DirectoryEnumerationFilters();
            f.InclusionFilter = fsei => fsei.Extension.ToUpperInvariant() == ".MAP";

            f.RecursionFilter = entryInfo => !entryInfo.IsMountPoint && !entryInfo.IsSymbolicLink;

            f.ErrorFilter = (errorCode, errorMessage, pathProcessed) => true;

            var dirEnumOptions =
                DirectoryEnumerationOptions.Files | 
                DirectoryEnumerationOptions.SkipReparsePoints | DirectoryEnumerationOptions.ContinueOnException |
                DirectoryEnumerationOptions.BasicSearch;

            var mapFiles =
                Directory.EnumerateFileSystemEntries(mapPath, dirEnumOptions, f);

            var l = LogManager.GetLogger("LoadMaps");

            var deserializer = new DeserializerBuilder()
                .Build();

            EventLogMap eventMapFile;

            var errorMaps = new List<string>();

            foreach (var mapFile in mapFiles)
            {
                try
                {
                    var validator = new EventLogMapValidator();

                    eventMapFile = deserializer.Deserialize<EventLogMap>(Alphaleonis.Win32.Filesystem.File.ReadAllText(mapFile));

                    var validate = validator.Validate(eventMapFile);

                    if (DisplayValidationResults(validate, mapFile))
                    {
                        EventLogMaps.Add(eventMapFile.EventId,eventMapFile);
                    }
                    else
                    {
                        errorMaps.Add(Path.GetFileName(mapFile));
                    }

                }
                catch (SyntaxErrorException se)
                {
                    Console.WriteLine();
                    l.Warn($"Syntax error in '{mapFile}':");
                    l.Fatal(se.Message);

                    var lines = File.ReadLines(mapFile).ToList();
                    var fileContents = mapFile.ReadAllText();

                    var badLine = lines[se.Start.Line - 1];
                    Console.WriteLine();
                    l.Fatal(
                        $"Bad line (or close to it) '{badLine}' has invalid data at column '{se.Start.Column}'");

                    if (fileContents.Contains('\t'))
                    {
                        Console.WriteLine();
                        l.Error(
                            "Bad line contains one or more tab characters. Replace them with spaces");
                        Console.WriteLine();
                        l.Info(fileContents.Replace("\t", "<TAB>"));
                    }

                //    hasError = true;
                }
                catch (YamlException ye)
                {
                    Console.WriteLine();
                    l.Warn($"Syntax error in '{mapFile}':");

                    var fileContents = mapFile.ReadAllText();

                    l.Info(fileContents);

                    if (ye.InnerException != null)
                    {
                        l.Fatal(ye.InnerException.Message);
                    }

                    Console.WriteLine();
                    l.Fatal("Verify all properties against example files or manual and try again.");

                  //  hasError = true;
                }
                catch (Exception e)
                {
                    l.Error($"Error loading map file '{mapFile}': {e.Message}");
                }
            }

            if (errorMaps.Count > 0)
            {
                l.Error("The following maps had errors. Scroll up to review errors, correct them, and try again.");
                foreach (var errorMap in errorMaps)
                {
                    l.Info(errorMap);
                }
                Environment.Exit(0);
            }


        }

        private static bool DisplayValidationResults(ValidationResult result, string source)
        {
            var l = LogManager.GetLogger("LoadMaps");
            l.Trace($"Performing validation on '{source}': {result.Dump()}");
            if (result.Errors.Count == 0)
            {
                return true;
            }

            Console.WriteLine();
            l.Error($"{source} had validation errors:");

            //   _loggerCopyLog.Error($"\r\n{source} had validation errors:");

            foreach (var validationFailure in result.Errors)
            {
                l.Error(validationFailure);
            }

            Console.WriteLine();
            l.Error("\r\nCorrect the errors and try again. Exiting");
            //   _loggerCopyLog.Error("Correct the errors and try again. Exiting");

            return false;
        }

        public int TotalEventLogs { get; }

        public long NextRecordId { get; }

        public List<ChunkInfo> Chunks { get; }

        public long FirstChunkNumber { get; }
        public long LastChunkNumber { get; }

        public short ChunkCount { get; }

        public int Crc { get; }
        public int CalculatedCrc { get; }

        public EventLogFlag Flags { get; }

        public short MinorVersion { get; }
        public short MajorVersion { get; }

        public Dictionary<long, string> ErrorRecords { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Version: {MajorVersion}.{MinorVersion}");
            sb.AppendLine($"Flags: {Flags}");
            sb.AppendLine($"Chunk count: {ChunkCount}");
            sb.AppendLine($"First/last Chunk #: {FirstChunkNumber}/{LastChunkNumber}");
            sb.AppendLine($"Stored CRC: {Crc:X}");
            sb.AppendLine($"Calculated CRC: {CalculatedCrc:X}");
            sb.AppendLine($"Total event log records found: {TotalEventLogs:N0}");

            return sb.ToString();
        }

        public IEnumerable<EventRecord> GetEventRecords()
        {
            foreach (var chunkInfo in Chunks)
            {
                foreach (var chunkInfoEventRecord in chunkInfo.EventRecords)
                {
                    yield return chunkInfoEventRecord;
                }
            }
        }
    }
}