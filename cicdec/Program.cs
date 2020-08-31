using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.BZip2;
using BioLib;
using BioLib.Streams;

namespace cicdec {
	class Program {
		private const string VERSION = "2.1.1";
		private const string PROMPT_ID = "cicdec_overwrite";

		private const int SEARCH_BUFFER_SIZE = 1024 * 1024;
		private const int BLOCK_HEADER_SIZE = 16 + 16 + 32;
		private static readonly byte[] DATA_SECTION_SIGNATURE = {0x77, 0x77, 0x67, 0x54, 0x29, 0x48};

		private static string inputFile;
		private static string outputDirectory;
		private static bool dumpBlocks;
		private static bool simulate;
		private static int installerVersion = -1;

		private static MemoryStream fileListStream;
		private static List<FileInfo> filesInfos;
		private static long dataBlockStartPosition = -1;
		private static int failedExtractions = 0;

		static void Main(string[] args) {
			const string USAGE = "[<options>...] <installer> [<output_directory>]\n\n" + 
								 "<options>:\n" + 
								 "  -v <version>\tExtract as installer version <version>. Auto-detection might not always work correctly, so it is possible to explicitly set the installer version.\n\n" + 
								 "  -db\tDump blocks. Save additional installer data like registry changes, license files and the uninstaller. This is considered raw data and might not be readable or usable.\n\n" + 
								 "  -si\tSimulate extraction without writing files to disk.";
			Bio.Header("cicdec - A Clickteam Install Creator unpacker", VERSION, "2019-2020", "Extracts files from installers made with Clickteam Install Creator", USAGE);
			
			inputFile = ParseCommandLine(args);
			var inputStream = File.OpenRead(inputFile);
			var binaryReader = new BinaryReader(inputStream);
			var inputStreamLength = inputStream.Length;

			// First we need to find the data section. The simplest way to do so
			// is searching for the signature 0x77, 0x77, 0x67, 0x54, 0x29, 0x48
			var startOffset = FindStartOffset(binaryReader);
			if (startOffset < 0) Bio.Error("Failed to find overlay signature.", Bio.EXITCODE.INVALID_INPUT);

			Bio.Cout("Starting extraction at offset " + startOffset + "\n");

			inputStream.Position = startOffset;

			// The data section consists of a varying number of data blocks,
			// whose headers give information about the type of data contained inside.
			while (inputStream.Position + BLOCK_HEADER_SIZE <= inputStreamLength) {
				var blockId = binaryReader.ReadUInt16();
				inputStream.Skip(2); // unknown
				var blockSize = binaryReader.ReadUInt32();
				var blockType = (BLOCK_TYPE) blockId;
				var nextBlockPos = inputStream.Position + blockSize;
				Bio.Cout(string.Format("Reading block 0x{0:X} {1,-16} with size {2}", blockId, (BLOCK_TYPE) blockId, blockSize));
				var outputFileName = string.Format("Block 0x{0:X} {1}.bin", blockId, (BLOCK_TYPE) blockId);

				if (blockType == BLOCK_TYPE.FILE_DATA) {
					// Data block should always be last, but better be safe and parse all other blocks before
					dataBlockStartPosition = inputStream.Position;
					
					if (dumpBlocks) {
						using (var ms = inputStream.Extract((int)blockSize)) {
							SaveToFile(ms, outputFileName);
						}
					}

					continue;
				}
				else if (blockType == BLOCK_TYPE.FILE_LIST) {
					fileListStream = UnpackStream(binaryReader, blockSize);
					if (fileListStream == null) Bio.Error("Failed to decompress file list", Bio.EXITCODE.RUNTIME_ERROR);

					if (dumpBlocks) SaveToFile(fileListStream, outputFileName);

					fileListStream.MoveToStart();
				}
				else if (dumpBlocks) {
					using (var decompressedStream = UnpackStream(binaryReader, blockSize)) {
						SaveToFile(decompressedStream, outputFileName);
					}
				}

				Bio.Debug("Pos: " + inputStream.Position + ", expected: " + nextBlockPos);
				inputStream.Position = nextBlockPos;
			}

			if (fileListStream == null) Bio.Error("File list could not be read. Please send a bug report if you want the file to be supported in a future version.", Bio.EXITCODE.NOT_SUPPORTED);

			if (dataBlockStartPosition > -1) {
				ParseFileList(fileListStream, inputStreamLength);
				ExtractFiles(inputStream, binaryReader);
			}
			else {
				// Install Creator supports external data files, instead of integrating 
				// the files into the executable. This actually means the data block is
				// saved as a separate file, which we just need to read.
				var dataFilePath = Path.Combine(Path.GetDirectoryName(inputFile), Path.GetFileNameWithoutExtension(inputFile) + ".D01");
				if (!File.Exists(dataFilePath)) Bio.Error("Could not find data block in installer and there is no external data file.", Bio.EXITCODE.RUNTIME_ERROR);

				Bio.Debug("External data file found");
				using (var dataFileStream = File.OpenRead(dataFilePath)) {
					ParseFileList(fileListStream, dataFileStream.Length);
					using (var dataFileBinaryReader = new BinaryReader(dataFileStream)) {
						dataBlockStartPosition = 0;
						ExtractFiles(dataFileStream, dataFileBinaryReader);
					}
				}
			}

			if (failedExtractions > 0) {
				if (failedExtractions == filesInfos.Count) Bio.Error("Extraction failed. The installer is either encrypted or a version, which is currently not supported.", Bio.EXITCODE.NOT_SUPPORTED);
				Bio.Warn(failedExtractions + " files failed to extract.");
			}
			else {
				Bio.Cout("All OK");
			}

			Bio.Pause();
		}

		static string ParseCommandLine(string[] args) {
			var count = args.Length - 1;
			if (count < 0) Bio.Error("No input file specified.", Bio.EXITCODE.INVALID_INPUT);

			var path = args[count];
			string inputFile;

			if (File.Exists(path)) {
				inputFile = path;
				outputDirectory = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
			}
			else {
				outputDirectory = path;
				count--;
				if (count < 0) Bio.Error("Invalid input file specified.", Bio.EXITCODE.INVALID_INPUT);
				inputFile = args[count];
			}

			if (!File.Exists(inputFile)) Bio.Error("Invalid input file specified.", Bio.EXITCODE.IO_ERROR);

			for (var i = 0; i < count; i++) {
				var arg = args[i];
				Bio.Debug("Argument: " + arg);
				switch (arg) {
					case "--dumpblocks":
					case "-db":
						dumpBlocks = true;
						break;
					case "--simulate":
					case "-si":
						simulate = true;
						break;
					case "--version":
					case "-v":
						i++;
						if (i >= args.Length) Bio.Error("No installer version specified.", Bio.EXITCODE.INVALID_PARAMETER);

						try {
							installerVersion = Convert.ToInt32(args[i]);
						}
						catch (FormatException) {
							Bio.Error("Invalid installer version specified.", Bio.EXITCODE.INVALID_PARAMETER);
						}

						break;
					default:
						Bio.Warn("Unknown command line option: " + arg);
						break;
				}
			}

			Bio.Debug("Input file: " + inputFile);
			Bio.Debug("Output directory: " + outputDirectory);
			Bio.Debug("Dump blocks: " + dumpBlocks);

			return inputFile;
		}

		static int GetInstallerVersion(Stream decompressedStream, BinaryReader binaryReader, ushort fileNumber,
			long dataStreamLength) {
			if (installerVersion > -1) return installerVersion;

			if (TestInstallerVersion("40 - Free", TryParse40, decompressedStream, binaryReader, fileNumber, dataStreamLength)) return 40;
			if (TestInstallerVersion("30 - Pro", TryParse30, decompressedStream, binaryReader, fileNumber, dataStreamLength)) return 30;
			if (TestInstallerVersion("20 - Legacy", TryParse20, decompressedStream, binaryReader, fileNumber, dataStreamLength)) return 20;

			Bio.Error($"Failed to determine installer version. Please send a bug report if you want the file to be supported in a future version.", Bio.EXITCODE.NOT_SUPPORTED);
			return -1;
		}

		static bool TestInstallerVersion(string version, Func<Stream, BinaryReader, FileInfo> parsingFunction, Stream decompressedStream, BinaryReader binaryReader, int fileNumber, long dataStreamLength) {
			Bio.Debug($"\nTesting installer version {version}\n");
			var pos = decompressedStream.Position;

			for (var i = 0; i < fileNumber; i++) {
				try {
					var fileInfo = parsingFunction(decompressedStream, binaryReader);

					Bio.Debug(string.Format("Node {0} at offset {1}, size: {2}, end: {3}", i, fileInfo.nodeStart, fileInfo.nodeSize, fileInfo.nodeEnd));

					if (!fileInfo.IsValid(dataStreamLength)) {
						Bio.Debug(fileInfo);
						Bio.Debug("Invalid file info");
						decompressedStream.Position = pos;
						return false;
					}

					if (fileInfo.type != 0) decompressedStream.Position = fileInfo.nodeEnd;
				}
				catch (EndOfStreamException) {
					Bio.Debug("End of Stream reached while parsing file list");
					decompressedStream.Position = pos;
					return false;
				}
			}

			decompressedStream.Position = pos;
			return true;
		}

		static int FindStartOffset(BinaryReader binaryReader) {
			byte[] buffer = new byte[SEARCH_BUFFER_SIZE];
			binaryReader.Read(buffer, 0, SEARCH_BUFFER_SIZE);

			for (int i = 0; i < SEARCH_BUFFER_SIZE; i++) {
				if (Match(buffer, i, DATA_SECTION_SIGNATURE)) return i + DATA_SECTION_SIGNATURE.Length;
			}

			return -1;
		}

		static bool Match(byte[] array, int pos, byte[] pattern) {
			if (pattern.Length > array.Length - pos) return false;

			for (int i = 0; i < pattern.Length; i++) {
				if (array[pos + i] != pattern[i]) return false;
			}

			return true;
		}

		static MemoryStream UnpackStream(BinaryReader binaryReader, uint blockSize, uint decompressedSize = 0, byte compressionMethod = byte.MaxValue) {
			if (decompressedSize == 0) decompressedSize = binaryReader.ReadUInt32();
			if (compressionMethod == byte.MaxValue) compressionMethod = binaryReader.ReadByte();
			Bio.Debug("Decompressing " + blockSize + " bytes @ " + binaryReader.BaseStream.Position);
			Bio.Debug(string.Format("\tCompression: {0}, decompressed size: {1}", (COMPRESSION) compressionMethod, decompressedSize));
			blockSize -= 5;
			var decompressedStream = new MemoryStream((int) decompressedSize);

			switch ((COMPRESSION) compressionMethod) {
				case COMPRESSION.NONE:
					binaryReader.BaseStream.Copy(decompressedStream, (int) blockSize);
					break;
				case COMPRESSION.DEFLATE:
					binaryReader.BaseStream.Skip(2);
					using (var deflateStream = new DeflateStream(binaryReader.BaseStream, CompressionMode.Decompress, true)) {
						deflateStream.Copy(decompressedStream, (int) decompressedSize);
					}
					break;
				case COMPRESSION.BZ2:
					using (var bzip2Stream = new BZip2InputStream(binaryReader.BaseStream)) {
						bzip2Stream.IsStreamOwner = false;
						bzip2Stream.Copy(decompressedStream, (int) decompressedSize);
					}
					break;
				default:
					Bio.Warn("Unknown compression method, data might be encrypted and cannot be unpacked. Skipping block.");
					decompressedStream.Dispose();
					binaryReader.BaseStream.Skip(blockSize);
					return null;
			}

			//binaryReader.BaseStream.Skip(blockSize);
			return decompressedStream;
		}

		static bool SaveToFile(Stream stream, string fileName, FileInfo fileInfo = null) {
			if (simulate) return true;

			if (stream == null) {
				Bio.Warn("Failed to save stream to file. Stream is null");
				return false;
			}

			stream.MoveToStart();
			var filePath = Path.Combine(outputDirectory, fileName);
			//Bio.Cout("Saving decompressed block to " + outputFilePath);

			try {
				if (!stream.WriteToFile(filePath, PROMPT_ID)) return true;

				if (fileInfo != null) {
					File.SetCreationTime(filePath, fileInfo.created);
					File.SetLastAccessTime(filePath, fileInfo.accessed);
					File.SetLastWriteTime(filePath, fileInfo.modified);
				}
			}
			catch (Exception e) {
				Bio.Warn("Failed to create file:" + e.Message);
				return false;
			}

			return true;
		}

		static void ParseFileList(Stream decompressedStream, long dataStreamLength) {
			var binaryReader  = new BinaryReader(decompressedStream);
			var fileNumber = binaryReader.ReadUInt16();
			filesInfos = new List<FileInfo>();
			decompressedStream.Skip(2); // Unknown. Maybe fileNumber is 4 bytes?
			Bio.Cout("\n" + fileNumber + " files in installer\n");

			installerVersion = GetInstallerVersion(decompressedStream, binaryReader, fileNumber, dataStreamLength);
			Func<Stream, BinaryReader, FileInfo> parsingFunction = TryParse30;
			if (installerVersion >= 40) {
				parsingFunction = TryParse40;
			}
			else if (installerVersion >= 30) {
				parsingFunction = TryParse30;
			}
			else if (installerVersion >= 20) {
				parsingFunction = TryParse20;
			}
			else {
				Bio.Error($"Unsupported installer version {installerVersion}. Please send a bug report if you want the file to be supported in a future version.", Bio.EXITCODE.NOT_SUPPORTED);
			}

			Bio.Cout($"\nStarting extraction as installer version {installerVersion}\n");
			for (var i = 0; i < fileNumber; i++) {
				var fileInfo = parsingFunction(decompressedStream, binaryReader);
				Bio.Debug(string.Format("Node {0} at offset {1}, size: {2}, end: {3}", i, fileInfo.nodeStart, fileInfo.nodeSize, fileInfo.nodeEnd));

				if (!fileInfo.IsValid(dataStreamLength)) Bio.Error($"The file could not be extracted as installer version {installerVersion}. Please try to manually set the correct version using the command line switch -v.", Bio.EXITCODE.RUNTIME_ERROR);

#if DEBUG
				if (dumpBlocks) {
					using (var ms = new MemoryStream((int) fileInfo.nodeSize)) {
						decompressedStream.Position = fileInfo.nodeStart;
						decompressedStream.Copy(ms, (int) fileInfo.nodeSize);
						decompressedStream.Position = fileInfo.nodeEnd;
						SaveToFile(ms, "FileMeta" + i + ".bin");
					}
				}
#endif

				if (fileInfo.type != 0) {
					decompressedStream.Position = fileInfo.nodeEnd;
					continue;
				}

				filesInfos.Add(fileInfo);
				Bio.Debug(fileInfo);
			}
		}

		static FileInfo TryParse20(Stream decompressedStream, BinaryReader binaryReader) {
			var fileInfo = new FileInfo(decompressedStream.Position, binaryReader.ReadUInt16(), binaryReader.ReadUInt16());
			if (fileInfo.type != 0) return fileInfo;

			decompressedStream.Skip(2);
			fileInfo.SetFileInfos(binaryReader.ReadUInt32(), binaryReader.ReadUInt32(), binaryReader.ReadUInt32(), binaryReader.ReadUInt32());
			decompressedStream.Skip(16);
			fileInfo.SetFileTimes(binaryReader.ReadInt64(), binaryReader.ReadInt64(), binaryReader.ReadInt64());

			ReadFilePath(decompressedStream, binaryReader, fileInfo);

			return fileInfo;
		}

		static FileInfo TryParse30(Stream decompressedStream, BinaryReader binaryReader) {
			var fileInfo = new FileInfo(decompressedStream.Position, binaryReader.ReadUInt16(), binaryReader.ReadUInt16());
			if (fileInfo.type != 0) return fileInfo;

			decompressedStream.Skip(2);
			fileInfo.SetFileInfos(binaryReader.ReadUInt32(), binaryReader.ReadUInt32(), binaryReader.ReadUInt32(), binaryReader.ReadUInt32());
			decompressedStream.Skip(18);
			fileInfo.index = binaryReader.ReadUInt32();
			fileInfo.SetFileTimes(binaryReader.ReadInt64(), binaryReader.ReadInt64(), binaryReader.ReadInt64());

			ReadFilePath(decompressedStream, binaryReader, fileInfo);

			return fileInfo;
		}

		static FileInfo TryParse40(Stream decompressedStream, BinaryReader binaryReader) {
			var fileInfo = new FileInfo(decompressedStream.Position, binaryReader.ReadUInt32(), binaryReader.ReadUInt16());
			if (fileInfo.type != 0) return fileInfo;

			decompressedStream.Skip(3);
			// Empty dummy files are missing file time attributes,
			// so the node is shorter than usual
			if (binaryReader.ReadByte() == 0xE2) {
				decompressedStream.Skip(30);
			}
			else {
				decompressedStream.Skip(14);
				var uncompressedSize = binaryReader.ReadUInt32();
				var offset = binaryReader.ReadUInt32();
				var compressedSize = binaryReader.ReadUInt32();
				decompressedStream.Skip(4);
				fileInfo.SetFileInfos(offset, compressedSize, 0, uncompressedSize);
				fileInfo.SetFileTimes(binaryReader.ReadInt64(), binaryReader.ReadInt64(), binaryReader.ReadInt64());
			}

			ReadFilePath(decompressedStream, binaryReader, fileInfo);
			Bio.Debug(fileInfo);
			return fileInfo;
		}

		static void ReadFilePath(Stream decompressedStream, BinaryReader binaryReader, FileInfo fileInfo) {
			// The path is always the last entry in the node, only followed by 9 
			// zero bytes (or less, see comment below)
			var pathLength = fileInfo.nodeEnd - decompressedStream.Position;
			if (pathLength < 1) return; // Prevent crashes when testing different installer versions
			
			var pathBuffer = new byte[pathLength];
			binaryReader.Read(pathBuffer, 0, (int) pathLength);

			// If the installer should create a shortcut for a file, its name is 
			// placed right after the output path. To make sure we don't have parts
			// of the .lnk file name in our buffer, we search for a 0 byte seperator
			// and split the array (ignoring the shortcut).
			// Normal files (without shortcut) always end in 9 zero bytes.
			var zeroBytePos = FindFirstZeroByte(pathBuffer);
			if (zeroBytePos > -1) pathBuffer = pathBuffer.Take(zeroBytePos).ToArray();

			fileInfo.path = Encoding.Default.GetString(pathBuffer);
			//Bio.Debug(decompressedStream.Position + ", length: " + pathLength);
		}

		static int FindFirstZeroByte(byte[] bytes) {
			for (var i = 0; i < bytes.Length; i++) {
				if (bytes[i] == 0) return i;
			}
			return -1;
		}

		static void ExtractFiles(Stream inputStream, BinaryReader binaryReader) {
			inputStream.Position = dataBlockStartPosition + 4;
			
			for (var i = 0; i < filesInfos.Count; i++) {
				var fileInfo = filesInfos[i];
				Bio.Debug(fileInfo);
				inputStream.Position = dataBlockStartPosition + fileInfo.offset + 4;
				Bio.Cout(string.Format("{0}/{1}\t{2}", i + 1, filesInfos.Count, fileInfo.path));

				// Empty files
				if (fileInfo.uncompressedSize == 0) {
					using (var ms = new MemoryStream()) {
						SaveToFile(ms, fileInfo.path);
					}
					continue;
				}

				try {
					using (var fileData = UnpackStream(binaryReader, fileInfo.compressedSize - 7, fileInfo.uncompressedSize)) {
						if (!SaveToFile(fileData, fileInfo.path, fileInfo)) throw new StreamUnsupportedException("Failed to decompress data");
					}
				}
				catch (Exception e) {
					Bio.Warn("Failed to decompress file, exception was " + e.Message);
					failedExtractions++;
				}
			}
		}

		enum COMPRESSION {
			NONE = 0,
			DEFLATE = 1,
			BZ2 = 2
		}

		enum BLOCK_TYPE {
			FILE_LIST = 0x143A,
			FILE_DATA = 0x7F7F,
			STRINGS = 0x143E,
			UNKNOWN_FONT = 0x1435,
			UNKNOWN_DATA = 0x1436,
			BACKGROUND_IMAGE = 0x1437,
			UNKNOWN_NUMBERS = 0x1444,
			REGISTRY_CHANGES = 0x1445,
			UNINSTALLER = 0x143F
		}
	}
}
