using System;
using BioLib;

namespace cicdec {
	public class FileInfo {
		public long nodeStart;
		public uint nodeSize;
		public long nodeEnd;
		public ushort type;
		public uint compressedSize;
		public uint offset;
		public uint unknown32;
		public uint uncompressedSize;
		public uint index;
		public string path;
		public DateTime modified;
		public DateTime accessed;
		public DateTime created;

		public FileInfo(long nodeStart, uint nodeSize, ushort type) {
			SetNodeInfos(nodeStart, nodeSize);
			this.type = type;
		}

		public FileInfo(ushort type, uint offset, uint compressedSize, uint unknown32, uint uncompressedSize) {
			this.type = type;
			this.compressedSize = compressedSize;
			this.offset = offset;
			this.unknown32 = unknown32;
			this.uncompressedSize = uncompressedSize;
		}

		public void SetFileInfos(uint offset, uint compressedSize, uint unknown32, uint uncompressedSize) {
			this.compressedSize = compressedSize;
			this.offset = offset;
			this.unknown32 = unknown32;
			this.uncompressedSize = uncompressedSize;
		}

		public void SetNodeInfos(long startPos, uint size) {
			nodeStart = startPos;
			nodeSize = size;
			nodeEnd = nodeStart + nodeSize;
		}

		public void SetFileTimes(long modified, long accessed, long created) {
			try {
				this.modified = DateTime.FromFileTime(modified);
				this.accessed = DateTime.FromFileTime(accessed);
				this.created = DateTime.FromFileTime(created);
			}
			catch (Exception) {
				Bio.Debug("Failed to parse file time");
			}
		}

		public bool IsValid(long fileStreamLength) {
			if (offset > fileStreamLength || (compressedSize > 0 && uncompressedSize / compressedSize > 1000) || index > 1000000000) return false;
			return true;
		}

		public override string ToString() {
			return string.Format("File {0}, type: {1:x} at {2}: {3} -> {4}, path: {5}", index, type, offset,
				compressedSize, uncompressedSize, path);
		}
	}
}