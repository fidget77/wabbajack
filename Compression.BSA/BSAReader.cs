﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using K4os.Compression.LZ4.Streams;
using File = Alphaleonis.Win32.Filesystem.File;

namespace Compression.BSA
{
    public enum VersionType : uint
    {
        TES4 = 0x67,
        FO3 = 0x68, // FO3, FNV, TES5
        SSE = 0x69,
        FO4 = 0x01
    };

    [Flags]
    public enum ArchiveFlags : uint
    {
        HasFolderNames = 0x1,
        HasFileNames = 0x2,
        Compressed = 0x4,
        Unk4 = 0x8,
        Unk5 = 0x10,
        Unk6 = 0x20,
        XBox360Archive = 0x40,
        Unk8 = 0x80,
        HasFileNameBlobs = 0x100,
        Unk10 = 0x200,
        Unk11 = 0x400
    }

    [Flags]
    public enum FileFlags : uint
    {
        Meshes = 0x1,
        Textures = 0x2,
        Menus = 0x4,
        Sounds = 0x8,
        Voices = 0x10,
        Shaders = 0x20,
        Trees = 0x40,
        Fonts = 0x80,
        Miscellaneous = 0x100
    }

    public class BSAReader : IDisposable
    {
        private Stream _stream;
        private BinaryReader _rdr;
        private string _magic;
        private uint _version;
        private uint _folderRecordOffset;
        private uint _archiveFlags;
        private uint _folderCount;
        private uint _fileCount;
        private uint _totalFolderNameLength;
        private uint _totalFileNameLength;
        private uint _fileFlags;
        private List<FolderRecord> _folders;
        internal string _fileName;

        public IEnumerable<FileRecord> Files
        {
            get
            {
                foreach (var folder in _folders)
                    foreach (var file in folder._files)
                        yield return file;
            }
        }

        public VersionType HeaderType
        {
            get
            {
                return (VersionType)_version;
            }
        }

        public ArchiveFlags ArchiveFlags
        {
            get
            {
                return (ArchiveFlags)_archiveFlags;
            }
        }

        public FileFlags FileFlags
        {
            get
            {
                return (FileFlags)_archiveFlags;
            }
        }


        public bool HasFolderNames
        {
            get
            {
                return (_archiveFlags & 0x1) > 0;
            }
        }

        public bool HasFileNames
        {
            get
            {
                return (_archiveFlags & 0x2) > 0;
            }
        }

        public bool CompressedByDefault
        {
            get
            {
                return (_archiveFlags & 0x4) > 0;
            }
        }

        public bool Bit9Set
        {
            get
            {
                return (_archiveFlags & 0x100) > 0;
            }
        }

        public bool HasNameBlobs
        {
            get
            {
                if (HeaderType == VersionType.FO3 || HeaderType == VersionType.SSE)
                {
                    return (_archiveFlags & 0x100) > 0;
                }
                return false;
            }
        }

        public BSAReader(string filename) : this(File.OpenRead(filename))
        {
            _fileName = filename;

        }

        public BSAReader(Stream stream)
        {
            _stream = stream;
            _rdr = new BinaryReader(_stream);
            LoadHeaders();
        }

        public void Dispose()
        {
            _stream.Close();
        }

        private void LoadHeaders()
        {
            var fourcc = Encoding.ASCII.GetString(_rdr.ReadBytes(4));

            if (fourcc != "BSA\0")
                throw new InvalidDataException("Archive is not a BSA");

            _magic = fourcc;
            _version = _rdr.ReadUInt32();
            _folderRecordOffset = _rdr.ReadUInt32();
            _archiveFlags = _rdr.ReadUInt32();
            _folderCount = _rdr.ReadUInt32();
            _fileCount = _rdr.ReadUInt32();
            _totalFolderNameLength = _rdr.ReadUInt32();
            _totalFileNameLength = _rdr.ReadUInt32();
            _fileFlags = _rdr.ReadUInt32();

            LoadFolderRecords();
        }

        private void LoadFolderRecords()
        {
            _folders = new List<FolderRecord>();
            for (int idx = 0; idx < _folderCount; idx += 1)
                _folders.Add(new FolderRecord(this, _rdr));

            foreach (var folder in _folders)
                folder.LoadFileRecordBlock(this, _rdr);

            foreach (var folder in _folders)
                foreach (var file in folder._files)
                    file.LoadFileRecord(this, folder, file, _rdr);

        }
    }

    public class FolderRecord
    {
        private ulong _nameHash;
        private uint _fileCount;
        private uint _unk;
        private ulong _offset;
        internal List<FileRecord> _files;

        internal FolderRecord(BSAReader bsa, BinaryReader src)
        {
            _nameHash = src.ReadUInt64();
            _fileCount = src.ReadUInt32();
            if (bsa.HeaderType == VersionType.SSE)
            {
                _unk = src.ReadUInt32();
                _offset = src.ReadUInt64();
            }
            else
            {
                _offset = src.ReadUInt32();
            }
        }

        public string Name { get; private set; }
        public ulong Hash
        {
            get
            {
                return _nameHash;
            }
        }

        internal void LoadFileRecordBlock(BSAReader bsa, BinaryReader src)
        {
            if (bsa.HasFolderNames)
            {
                Name = src.ReadStringLen(bsa.HeaderType);
            }

            _files = new List<FileRecord>();
            for (int idx = 0; idx < _fileCount; idx += 1)
            {
                _files.Add(new FileRecord(bsa, this, src));
            }
        }

    }

    public class FileRecord
    {
        private BSAReader _bsa;
        private ulong _hash;
        private bool _compressedFlag;
        private uint _size;
        private uint _offset;
        private FolderRecord _folder;
        private string _name;
        private uint _originalSize;
        private uint _dataSize;
        private uint _onDiskSize;
        private string _nameBlob;
        private long _dataOffset;

        public FileRecord(BSAReader bsa, FolderRecord folderRecord, BinaryReader src)
        {
            _bsa = bsa;
            _hash = src.ReadUInt64();
            var size = src.ReadUInt32();
            _compressedFlag = (size & (0x1 << 30)) > 0;

            if (_compressedFlag)
                _size = size ^ (0x1 << 30);
            else
                _size = size;

            if (Compressed)
                _size -= 4;

            _offset = src.ReadUInt32();
            _folder = folderRecord;

            var old_pos = src.BaseStream.Position;

            src.BaseStream.Position = _offset;

            if (bsa.HasNameBlobs)
                _nameBlob = src.ReadStringLenNoTerm(bsa.HeaderType);

            
            if (Compressed)
                _originalSize = src.ReadUInt32();

            _onDiskSize = (uint)(_size - (_nameBlob == null ? 0 : _nameBlob.Length + 1));

            if (Compressed)
            {
                _dataSize = _originalSize;
                _onDiskSize -= 4;
            }
            else
                _dataSize = _onDiskSize;

            _dataOffset = src.BaseStream.Position;

            src.BaseStream.Position = old_pos;
        }

        internal void LoadFileRecord(BSAReader bsaReader, FolderRecord folder, FileRecord file, BinaryReader rdr)
        {
            _name = rdr.ReadStringTerm(_bsa.HeaderType);
        }

        public string Path
        {
            get
            {
                if (string.IsNullOrEmpty(_folder.Name)) return _name;
                return _folder.Name + "\\" + _name;
            }
        }

        public bool Compressed
        {
            get
            {
                if (_compressedFlag) return !_bsa.CompressedByDefault;
                return _bsa.CompressedByDefault;
            }
        }

        public int Size
        {
            get
            {
                return (int)_dataSize;
            }
        }

        public ulong Hash {
            get
            {
                return _hash;
            }
        }

        public FolderRecord Folder
        {
            get
            {
                return _folder;
            } 
        }

        public bool FlipCompression { get => _compressedFlag; }

        public void CopyDataTo(Stream output)
        {
            using (var in_file = File.OpenRead(_bsa._fileName))
            using (var rdr = new BinaryReader(in_file))
            {
                rdr.BaseStream.Position = _dataOffset;

                if (_bsa.HeaderType == VersionType.SSE)
                {

                    if (Compressed)
                    {
                        var r = LZ4Stream.Decode(rdr.BaseStream);
                        r.CopyToLimit(output, (int)_originalSize);

                    }
                    else
                    {
                        rdr.BaseStream.CopyToLimit(output, (int)_onDiskSize);
                    }
                }
                else
                {

                    if (Compressed)
                    {
                        using (var z = new InflaterInputStream(rdr.BaseStream))
                            z.CopyToLimit(output, (int)_originalSize);

                    }
                    else
                    {
                        rdr.BaseStream.CopyToLimit(output, (int)_onDiskSize);
                    }
                }
            }
        }

        public byte[] GetData()
        {
            var ms = new MemoryStream();
            CopyDataTo(ms);
            return ms.ToArray();
        }


    }
}
