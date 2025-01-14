﻿using Compression.BSA;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using Alphaleonis.Win32.Filesystem;
using Wabbajack.Common;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using DirectoryInfo = Alphaleonis.Win32.Filesystem.DirectoryInfo;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace VFS
{
    public class VirtualFileSystem
    {

        internal static string _stagedRoot;
        public static VirtualFileSystem VFS;
        private Dictionary<string, VirtualFile> _files = new Dictionary<string, VirtualFile>();
        private bool _disableDiskCache;
        private volatile bool _isSyncing;

        public static string RootFolder { get; }
        public Dictionary<string, IEnumerable<VirtualFile>> HashIndex { get; private set; }

        static VirtualFileSystem()
        {
            VFS = new VirtualFileSystem();
            RootFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            _stagedRoot = Path.Combine(RootFolder, "vfs_staged_files");
            Utils.OnQueue(() =>
            {
                if (Directory.Exists(_stagedRoot))
                    DeleteDirectory(_stagedRoot);
            });

            Directory.CreateDirectory(_stagedRoot);
        }

        private static void DeleteDirectory(string path)
        {
            Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .DoProgress("Cleaning VFS Files", file =>
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        fi.Attributes &= ~FileAttributes.ReadOnly;
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        Utils.Log(ex.ToString());
                    }
                });

            Directory.EnumerateDirectories(path, DirectoryEnumerationOptions.Recursive)
                .OrderByDescending(d => d.Length)
                .DoProgress("Cleaning VFS Folders", folder =>
                {
                    try
                    {
                        if (!Directory.Exists(folder))
                            return;
                        var di = new DirectoryInfo(folder);
                        di.Attributes &= ~FileAttributes.ReadOnly;
                        Directory.Delete(path, true);
                    }
                    catch (Exception ex)
                    {
                        Utils.Log(ex.ToString());
                    }
                });
            

        }

        public VirtualFileSystem ()
        {
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                Utils.Log("Loading VFS Cache");
                if (!File.Exists("vfs_cache.bin")) return;
                _files = new Dictionary<string, VirtualFile>();

                try
                {
                    using (var fs = File.OpenRead("vfs_cache.bin"))
                    using (var br = new BinaryReader(fs))
                    {
                        while (true)
                        {

                            var fr = VirtualFile.Read(br);
                            _files.Add(fr.FullPath, fr);
                        }
                    }
                }
                catch (EndOfStreamException ex)
                {
                }


                CleanDB();
            }
            catch (Exception ex)
            {
                Utils.Log($"Purging cache due to {ex}");
                File.Delete("vfs_cache.bson");
                _files.Clear();
            }
        }

        public void SyncToDisk()
        {
            if (!_disableDiskCache)
            {
                Utils.Status("Syncing VFS Cache");
                lock (this)
                {
                    try
                    {
                        _isSyncing = true;

                        if (File.Exists("vfs_cache.bin_new"))
                            File.Delete("vfs_cache.bin_new");

                        using (var fs = File.OpenWrite("vfs_cache.bin_new"))
                        using (var bw = new BinaryWriter(fs))
                        {
                            Utils.Log($"Syncing VFS to Disk: {_files.Count} entries");
                            foreach (var f in _files.Values)
                            {
                                f.Write(bw);
                            }
                        }

                        if (File.Exists("vfs_cache.bin"))
                            File.Delete("vfs_cache.bin");

                        File.Move("vfs_cache.bin_new", "vfs_cache.bin");
                    }
                    finally
                    {
                        _isSyncing = false;
                    }
                }
            }
        }

        public IList<VirtualFile> FilesInArchive(VirtualFile f)
        {
            var path = f.FullPath + "|";
            return _files.Values
                            .Where(v => v.FullPath.StartsWith(path))
                            .ToList();
        }


        public void Purge(VirtualFile f)
        {
            var path = f.FullPath + "|";
            lock (this)
            {
                _files.Values
                      .Where(v => v.FullPath.StartsWith(path) || v.FullPath == f.FullPath)
                      .ToList()
                      .Do(r => {
                          _files.Remove(r.FullPath);
                          });
            }
        }

        public void Add(VirtualFile f)
        {
            lock (this)
            {
                if (_files.ContainsKey(f.FullPath))
                    Purge(f);
                _files.Add(f.FullPath, f);
            }
        }

        public VirtualFile Lookup(string f)
        {
            lock (this)
            {
                if (_files.TryGetValue(f, out var found))
                    return found;
                return null;
            }
        }

        /// <summary>
        /// Remove any orphaned files in the DB.
        /// </summary>
        private void CleanDB()
        {
            Utils.Log("Cleaning VFS cache");
            lock (this)
            {
                _files.Values
                     .Where(f =>
                     {
                         if (f.IsConcrete)
                             return !File.Exists(f.StagedPath);
                         if (f.Hash == null)
                             return true;
                         while (f.ParentPath != null)
                         {
                             if (Lookup(f.ParentPath) == null)
                                 return true;
                             if (f.Hash == null)
                                 return true;
                             f = Lookup(f.ParentPath);
                         }
                         return false;
                     })
                     .ToList()
                     .Do(f => _files.Remove(f.FullPath));
            }
        }

        public void BackfillMissing()
        {
            lock(this)
            {
                _files.Values
                      .Select(f => f.ParentPath)
                      .Where(s => s != null)
                      .Where(s => !_files.ContainsKey(s))
                      .ToHashSet()
                      .Do(s =>
                      {
                          AddKnown(new VirtualFile() { Paths = s.Split('|') });
                      });
            }
        }

        /// <summary>
        /// Add a known file to the index, bit of a hack as we won't assume that all the fields for the archive are filled in. 
        /// you will need to manually update the SHA hash when you are done adding files, by calling `RefreshIndexes`
        /// </summary>
        /// <param name="virtualFile"></param>
        public void AddKnown(VirtualFile virtualFile)
        {
            lock(this)
            {
                // We don't know enough about these files to be able to store them in the disk cache 
                _disableDiskCache = true;
                _files[virtualFile.FullPath] = virtualFile;
            }
        }

        /// <summary>
        /// Adds the root path to the filesystem. This may take quite some time as every file in the folder will be hashed,
        /// and every archive examined.
        /// </summary>
        /// <param name="path"></param>
        public void AddRoot(string path)
        {
            IndexPath(path);
            RefreshIndexes();
        }

        public void RefreshIndexes()
        {
            Utils.Log("Building Hash Index");
            lock(this)
            {
                HashIndex = _files.Values
                                  .GroupBy(f => f.Hash)
                                  .ToDictionary(f => f.Key, f => (IEnumerable<VirtualFile>)f);
            }
        }

        private void IndexPath(string path)
        {
            var file_list = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            Utils.Log($"Updating the cache for {file_list.Count} files");
            file_list.PMap(f => UpdateFile(f));
            SyncToDisk();
        }

        private void UpdateFile(string f)
        {
        TOP:
            var lv = Lookup(f);
            if (lv == null)
            {
                Utils.Status($"Analyzing {f}");

                lv = new VirtualFile()
                {
                    Paths = new string[] { f }
                };

                lv.Analyze();
                Add(lv);
                if (lv.IsArchive)
                {
                    UpdateArchive(lv);
                }
                // Upsert after extraction incase extraction fails
            }
            if (lv.IsOutdated)
            {
                Purge(lv);
                goto TOP;
            }
        }

        private void UpdateArchive(VirtualFile f)
        {
            if (!f.IsStaged)
                throw new InvalidDataException("Can't analyze an unstaged file");

            var tmp_dir = Path.Combine(_stagedRoot, Guid.NewGuid().ToString());
            Utils.Status($"Extracting Archive {Path.GetFileName(f.StagedPath)}");

            FileExtractor.ExtractAll(f.StagedPath, tmp_dir);


            Utils.Status($"Updating Archive {Path.GetFileName(f.StagedPath)}");

            var entries = Directory.EnumerateFiles(tmp_dir, "*", SearchOption.AllDirectories)
                                   .Select(path => path.RelativeTo(tmp_dir));

            var new_files = entries.Select(e => {
                var new_path = new string[f.Paths.Length + 1];
                f.Paths.CopyTo(new_path, 0);
                new_path[f.Paths.Length] = e;
                var nf = new VirtualFile()
                {
                    Paths = new_path,
                };
                nf._stagedPath = Path.Combine(tmp_dir, e);
                Add(nf);
                return nf;
                }).ToList();

            // Analyze them
            new_files.PMap(file =>
            {
                Utils.Status($"Analyzing {Path.GetFileName(file.StagedPath)}");
                file.Analyze();
            });
            // Recurse into any archives in this archive
            new_files.Where(file => file.IsArchive).Do(file => UpdateArchive(file));

            f.FinishedIndexing = true;

            if (!_isSyncing)
                SyncToDisk();

            Utils.Status("Cleaning Directory");
            DeleteDirectory(tmp_dir);

        }

        public Action Stage(IEnumerable<VirtualFile> files)
        {
            var grouped = files.SelectMany(f => f.FilesInPath)
                               .Distinct()
                               .Where(f => f.ParentArchive != null)
                               .GroupBy(f => f.ParentArchive)
                               .OrderBy(f => f.Key == null ? 0 : f.Key.Paths.Length)
                               .ToList();

            List<string> Paths = new List<string>();

            foreach (var group in grouped)
            {
                var tmp_path = Path.Combine(_stagedRoot, Guid.NewGuid().ToString());
                FileExtractor.ExtractAll(group.Key.StagedPath, tmp_path);
                Paths.Add(tmp_path);
                foreach (var file in group)
                    file._stagedPath = Path.Combine(tmp_path, file.Paths[group.Key.Paths.Length]);

            }

            return () =>
            {
                Paths.Do(p =>
                {
                    if (Directory.Exists(p)) DeleteDirectory(p);
                });
            };
        }

        

        public StagingGroup StageWith(IEnumerable<VirtualFile> files)
        {
            var grp = new StagingGroup(files);
            grp.Stage();
            return grp;
        }

        public VirtualFile this[string path]
        {
            get
            {
                return Lookup(path);
            }
        }

        internal List<string> GetArchiveEntryNames(VirtualFile file)
        {
            if (!file.IsStaged)
                throw new InvalidDataException("File is not staged");

            if (file.Extension == ".bsa") {
                using (var ar = new BSAReader(file.StagedPath))
                {
                    return ar.Files.Select(f => f.Path).ToList();
                }
            }
            if (file.Extension == ".zip")
            {
                using (var s = new ZipFile(File.OpenRead(file.StagedPath)))
                {
                    s.IsStreamOwner = true;
                    s.UseZip64 = UseZip64.On;

                    if (s.OfType<ZipEntry>().FirstOrDefault(e => !e.CanDecompress) == null)
                    {
                        return s.OfType<ZipEntry>()
                                .Where(f => f.IsFile)
                                .Select(f => f.Name.Replace('/', '\\'))
                                .ToList();
                    }
                }
            }
            
            /*
            using (var e = new ArchiveFile(file.StagedPath))
            {
                return e.Entries
                        .Where(f => !f.IsFolder)
                        .Select(f => f.FileName).ToList();
            }*/
            return null;

        }

        /// <summary>
        /// Given a path that starts with a HASH, return the Virtual file referenced
        /// </summary>
        /// <param name="archiveHashPath"></param>
        /// <returns></returns>
        public VirtualFile FileForArchiveHashPath(string[] archiveHashPath)
        {
            if (archiveHashPath.Length == 1)
                return HashIndex[archiveHashPath[0]].First();

            var archive = HashIndex[archiveHashPath[0]].Where(a => a.IsArchive).OrderByDescending(a => a.LastModified).First();
            string fullPath = archive.FullPath + "|" + String.Join("|", archiveHashPath.Skip(1));
            return Lookup(fullPath);
        }
    }

    public class StagingGroup : List<VirtualFile>, IDisposable
    {
        public StagingGroup(IEnumerable<VirtualFile> files) : base(files)
        {
        }

        public void Dispose()
        {
            this.Do(f => f.Unstage());
        }

        internal void Stage()
        {
            VirtualFileSystem.VFS.Stage(this);
        }
    }

    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    public class VirtualFile
    {
        public string[] _paths;
        [JsonProperty]
        public string[] Paths
        {
            get
            {
                return _paths;
            }
            set
            {
                for (int idx = 0; idx < value.Length; idx += 1)
                    value[idx] = String.Intern(value[idx]);
                _paths = value;
            }
        }
        [JsonProperty]
        public string Hash { get; set; }
        [JsonProperty]
        public long Size { get; set; }
        [JsonProperty]
        public ulong LastModified { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? FinishedIndexing { get; set; }


        private string _fullPath;

        public VirtualFile()
        {
        }

        internal string _stagedPath;


        public string FullPath
        {
            get
            {
                if (_fullPath != null) return _fullPath;
                _fullPath = String.Join("|", Paths);
                return _fullPath;
            }
        }

        public string Extension
        {
            get
            {
                return Path.GetExtension(Paths.Last());
            }
        }

     


        /// <summary>
        /// If this file is in an archive, return the Archive File, otherwise return null.
        /// </summary>
        public VirtualFile TopLevelArchive
        {
            get
            {
                if (Paths.Length == 0) return null;
                return VirtualFileSystem.VFS[Paths[0]];
            }
        }

        public VirtualFile ParentArchive
        {
            get
            {
                if (ParentPath == null) return null;
                return VirtualFileSystem.VFS.Lookup(ParentPath);
            }
        }

        private bool? _isArchive;
        public bool IsArchive
        {
            get
            {
                if (_isArchive == null)
                    _isArchive = FileExtractor.CanExtract(Extension);
                return (bool)_isArchive;
            }
        }

        public bool IsStaged
        {
            get
            {
                if (IsConcrete) return true;
                return _stagedPath != null;
            }
        }

        public string StagedPath
        {
            get
            {
                if (!IsStaged)
                    throw new InvalidDataException("File is not staged");
                if (IsConcrete) return Paths[0];
                return _stagedPath;
            }
            set
            {
                if (IsStaged && value != null)
                    throw new InvalidDataException("Can't change the path of a already staged file");
                _stagedPath = value;
            }
        }

        public FileStream OpenRead()
        {
            if (!IsStaged)
                throw new InvalidDataException("File is not staged, cannot open");
            return File.OpenRead(_stagedPath);
        }

        /// <summary>
        /// Calulate the file's SHA, size and last modified
        /// </summary>
        internal void Analyze()
        {
            if (!IsStaged)
                throw new InvalidDataException("Cannot analyze an unstaged file");

            var fio = new FileInfo(StagedPath);
            Size = fio.Length;
            Hash = Utils.FileSHA256(StagedPath);
            LastModified = fio.LastWriteTime.ToMilliseconds();

        }


        /// <summary>
        /// Delete the temoporary file associated with this file
        /// </summary>
        internal void Unstage()
        {
            if (IsStaged && !IsConcrete)
            {
                File.Delete(_stagedPath);
                _stagedPath = null;
            }
        }

        internal string GenerateStagedName()
        {
            if (_stagedPath != null) return _stagedPath;
            _stagedPath = Path.Combine(VirtualFileSystem._stagedRoot, Guid.NewGuid().ToString() + Path.GetExtension(Paths.Last()));
            return _stagedPath;
        }

        /// <summary>
        /// Returns true if this file always exists on-disk, and doesn't need to be staged.
        /// </summary>
        public bool IsConcrete
        {
            get
            {
                return Paths.Length == 1;
            }
        }

        public bool IsOutdated
        {
            get
            {
                if (IsStaged)
                {
                    var fi = new FileInfo(StagedPath);
                    if (fi.LastWriteTime.ToMilliseconds() != LastModified || fi.Length != Size)
                        return true;
                    if (IsArchive)
                        if (!FinishedIndexing ?? true)
                            return true;
                }
                return false;
            }
                
        }

        private string _parentPath;
        public string ParentPath 
        {
          get {
                if (_parentPath == null && !IsConcrete)
                    _parentPath = String.Join("|", Paths.Take(Paths.Length - 1));
                return _parentPath;
          }
        }

        public IEnumerable<VirtualFile> FileInArchive
        {
            get
            {
                return VirtualFileSystem.VFS.FilesInArchive(this);
            }
        }

        public string[] MakeRelativePaths()
        {
            var path_copy = (string[])Paths.Clone();
            path_copy[0] = VirtualFileSystem.VFS.Lookup(Paths[0]).Hash;
            return path_copy;
        }

        public IEnumerable<VirtualFile> FilesInPath
        {
            get {
                return Enumerable.Range(1, Paths.Length)
                                 .Select(i => Paths.Take(i))
                                 .Select(path => VirtualFileSystem.VFS.Lookup(String.Join("|", path)));
                                    
            }
        }

        public void Write(BinaryWriter bw)
        {
            bw.Write(FullPath);
            bw.Write(Hash ?? "");
            bw.Write(Size);
            bw.Write(LastModified);
            bw.Write(FinishedIndexing ?? false);
        }

        public static VirtualFile Read(BinaryReader rdr)
        {
            var vf = new VirtualFile();
            var full_path = rdr.ReadString();
            vf.Paths = full_path.Split('|');

            for (int x = 0; x < vf.Paths.Length; x++)
                vf.Paths[x] = String.Intern(vf.Paths[x]);

            vf._fullPath = full_path;
            vf.Hash = rdr.ReadString();
            if (vf.Hash == "") vf.Hash = null;
            vf.Size = rdr.ReadInt64();
            vf.LastModified = rdr.ReadUInt64();
            vf.FinishedIndexing = rdr.ReadBoolean();
            if (vf.FinishedIndexing == false) vf.FinishedIndexing = null;
            return vf;

        }
    }


}
