﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ClientDBExtractor
{
    [Flags]
    public enum LocaleFlags
    {
        All = -1,
        None = 0,
        Unk_1 = 0x1,
        enUS = 0x2,
        koKR = 0x4,
        Unk_8 = 0x8,
        frFR = 0x10,
        deDE = 0x20,
        zhCN = 0x40,
        esES = 0x80,
        zhTW = 0x100,
        enGB = 0x200,
        enCN = 0x400,
        enTW = 0x800,
        esMX = 0x1000,
        ruRU = 0x2000,
        ptBR = 0x4000,
        itIT = 0x8000,
        ptPT = 0x10000
    }

    public class RootBlock
    {
        public uint Unk1;
        public LocaleFlags Flags;
    }

    public class RootEntry
    {
        public RootBlock Block;
        public int Unk1;
        public byte[] MD5;
        public ulong Hash;

        public override string ToString()
        {
            return String.Format("Block: {0:X8} {1:X8}, File: {2:X8} {3}", Block.Unk1, Block.Flags, Unk1, MD5.ToHexString());
        }
    }

    internal class EncodingEntry
    {
        public int Size;
        public List<byte[]> Keys;

        public EncodingEntry()
        {
            Keys = new List<byte[]>();
        }
    }

    internal class IndexEntry
    {
        public int Index;
        public int Offset;
        public int Size;
    }

    public class CASCHandler
    {
        static readonly ByteArrayComparer comparer = new ByteArrayComparer();

        public readonly Dictionary<ulong, List<RootEntry>> RootData = new Dictionary<ulong, List<RootEntry>>();
        readonly Dictionary<byte[], EncodingEntry> EncodingData = new Dictionary<byte[], EncodingEntry>(comparer);
        readonly Dictionary<byte[], IndexEntry> LocalIndexData = new Dictionary<byte[], IndexEntry>(comparer);

        public static readonly Jenkins96 Hasher = new Jenkins96();

        private readonly Dictionary<int, FileStream> DataStreams = new Dictionary<int, FileStream>();

        public int NumRootEntries { get { return RootData.Count; } }

        private readonly CASCConfig config;
        private readonly CDNHandler cdn;

        private CASCHandler(CASCConfig config, CDNHandler cdn, BackgroundWorker worker)
        {
            this.config = config;
            this.cdn = cdn;

            if (!config.OnlineMode)
            {
                var idxFiles = GetIdxFiles(this.config.BasePath);

                if (idxFiles.Count == 0)
                    throw new FileNotFoundException("idx files missing!");

                if (worker != null) worker.ReportProgress(0);

                int idxIndex = 0;

                foreach (var idx in idxFiles)
                {
                    using (var fs = new FileStream(idx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var br = new BinaryReader(fs))
                    {
                        int h2Len = br.ReadInt32();
                        int h2Check = br.ReadInt32();
                        byte[] h2 = br.ReadBytes(h2Len);

                        long padPos = (8 + h2Len + 0x0F) & 0xFFFFFFF0;
                        fs.Position = padPos;

                        int dataLen = br.ReadInt32();
                        int dataCheck = br.ReadInt32();

                        int numBlocks = dataLen / 18;

                        for (int i = 0; i < numBlocks; i++)
                        {
                            IndexEntry info = new IndexEntry();
                            byte[] key = br.ReadBytes(9);
                            int indexHigh = br.ReadByte();
                            int indexLow = br.ReadInt32BE();

                            info.Index = (int)((byte)(indexHigh << 2) | ((indexLow & 0xC0000000) >> 30));
                            info.Offset = (indexLow & 0x3FFFFFFF);
                            info.Size = br.ReadInt32();

                            // duplicate keys wtf...
                            //IndexData[key] = info; // use last key
                            if (!LocalIndexData.ContainsKey(key)) // use first key
                                LocalIndexData.Add(key, info);
                        }

                        padPos = (dataLen + 0x0FFF) & 0xFFFFF000;
                        fs.Position = padPos;

                        fs.Position += numBlocks * 18;

                        if (fs.Position != fs.Position)
                            throw new Exception("idx file under read");
                    }

                    if (worker != null) worker.ReportProgress((int)((float)++idxIndex / (float)idxFiles.Count * 100));
                }

                Logger.WriteLine("CASCHandler: loaded {0} indexes", LocalIndexData.Count);
            }

            if (worker != null) worker.ReportProgress(0);

            using (var fs = OpenEncodingFile())
            using (var br = new BinaryReader(fs))
            {
                br.ReadBytes(2); // EN
                byte b1 = br.ReadByte();
                byte b2 = br.ReadByte();
                byte b3 = br.ReadByte();
                ushort s1 = br.ReadUInt16();
                ushort s2 = br.ReadUInt16();
                int numEntries = br.ReadInt32BE();
                int i1 = br.ReadInt32BE();
                byte b4 = br.ReadByte();
                int entriesOfs = br.ReadInt32BE();

                fs.Position += entriesOfs; // skip strings
                fs.Position += numEntries * 32;

                for (int i = 0; i < numEntries; ++i)
                {
                    ushort keysCount;

                    while ((keysCount = br.ReadUInt16()) != 0)
                    {
                        int fileSize = br.ReadInt32BE();
                        byte[] md5 = br.ReadBytes(16);

                        var entry = new EncodingEntry();
                        entry.Size = fileSize;

                        for (int ki = 0; ki < keysCount; ++ki)
                        {
                            byte[] key = br.ReadBytes(16);

                            entry.Keys.Add(key);
                        }

                        EncodingData.Add(md5, entry);
                    }

                    while (br.PeekChar() == 0)
                        fs.Position++;

                    if (worker != null) worker.ReportProgress((int)((float)fs.Position / (float)fs.Length * 100));
                }

                Logger.WriteLine("CASCHandler: loaded {0} encoding data", EncodingData.Count);
            }

            if (worker != null) worker.ReportProgress(0);

            using (var fs = OpenRootFile())
            using (var br = new BinaryReader(fs))
            {
                while (fs.Position < fs.Length)
                {
                    int count = br.ReadInt32();

                    RootBlock block = new RootBlock();
                    block.Unk1 = br.ReadUInt32();
                    block.Flags = (LocaleFlags)br.ReadUInt32();

                    if (block.Flags == LocaleFlags.None)
                        throw new Exception("block.Flags == LocaleFlags.None");

                    RootEntry[] entries = new RootEntry[count];

                    for (var i = 0; i < count; ++i)
                    {
                        entries[i] = new RootEntry();
                        entries[i].Block = block;
                        entries[i].Unk1 = br.ReadInt32();
                    }

                    for (var i = 0; i < count; ++i)
                    {
                        entries[i].MD5 = br.ReadBytes(16);

                        ulong hash = br.ReadUInt64();
                        entries[i].Hash = hash;

                        if (!RootData.ContainsKey(hash))
                        {
                            RootData[hash] = new List<RootEntry>();
                            RootData[hash].Add(entries[i]);
                        }
                        else
                            RootData[hash].Add(entries[i]);
                    }

                    if (worker != null) worker.ReportProgress((int)((float)fs.Position / (float)fs.Length * 100));
                }

                Logger.WriteLine("CASCHandler: loaded {0} root data", RootData.Count);
            }

            if (worker != null) worker.ReportProgress(0);
        }

        private Stream OpenRootFile()
        {
            var encInfo = GetEncodingInfo(config.RootMD5);

            if (encInfo == null)
                throw new FileNotFoundException("encoding info for root file missing!");

            if (encInfo.Keys.Count > 1)
                throw new FileNotFoundException("multiple encoding info for root file found!");

            Stream s = TryLocalCache(encInfo.Keys[0], config.RootMD5, "data\\root");

            if (s != null)
                return s;

            return OpenFile(encInfo.Keys[0]);
        }

        private Stream OpenEncodingFile()
        {
            Stream s = TryLocalCache(config.EncodingKey, config.EncodingMD5, "data\\encoding");

            if (s != null)
                return s;

            return OpenFile(config.EncodingKey);
        }

        private Stream TryLocalCache(byte[] key, byte[] md5, string name)
        {
            if (File.Exists(name))
            {
                var fs = File.OpenRead(name);

                if (MD5.Create().ComputeHash(fs).EqualsTo(md5))
                {
                    fs.Position = 0;
                    return fs;
                }

                fs.Close();

                File.Delete(name);
            }

            ExtractFile(key, ".", name);

            return null;
        }

        private Stream OpenFile(byte[] key)
        {
            try
            {
                if (config.OnlineMode)
                    throw new Exception();

                var idxInfo = GetLocalIndexInfo(key);

                if (idxInfo == null)
                    throw new Exception("local index missing");

                var stream = GetDataStream(idxInfo.Index);

                stream.Position = idxInfo.Offset;

                stream.Position += 30;

                using (BLTEHandler blte = new BLTEHandler(stream, idxInfo.Size - 30))
                {
                    return blte.OpenFile();
                }
            }
            catch
            {
                throw new Exception("CDN index missing");
            }
        }

        private MemoryStream ExtractFile(byte[] key, string path, string name)
        {
            try
            {
                if (config.OnlineMode)
                    throw new Exception();

                var idxInfo = GetLocalIndexInfo(key);

                if (idxInfo == null)
                    throw new Exception("local index missing");

                var stream = GetDataStream(idxInfo.Index);

                stream.Position = idxInfo.Offset;

                stream.Position += 30;

                using (BLTEHandler blte = new BLTEHandler(stream, idxInfo.Size - 30))
                {
                    return blte.OpenFile();
                }
            }
            catch
            {
                throw new Exception("CDN index missing");
            }
        }

        ~CASCHandler()
        {
            foreach (var stream in DataStreams)
                stream.Value.Close();
        }

        private static List<string> GetIdxFiles(string wowPath)
        {
            List<string> latestIdx = new List<string>();

            for (int i = 0; i < 0x10; ++i)
            {
                var files = Directory.EnumerateFiles(Path.Combine(wowPath, "Data\\data\\"), String.Format("{0:X2}*.idx", i));

                if (files.Count() > 0)
                    latestIdx.Add(files.Last());
            }

            return latestIdx;
        }

        public List<RootEntry> GetRootInfo(ulong hash)
        {
            List<RootEntry> result;
            RootData.TryGetValue(hash, out result);
            return result;
        }

        private EncodingEntry GetEncodingInfo(byte[] md5)
        {
            EncodingEntry result;
            EncodingData.TryGetValue(md5, out result);
            return result;
        }

        private IndexEntry GetLocalIndexInfo(byte[] key)
        {
            byte[] temp = key.Copy(9);

            IndexEntry result;
            if (!LocalIndexData.TryGetValue(temp, out result))
                Logger.WriteLine("CASCHandler: missing index: {0}", key.ToHexString());

            return result;
        }

        private FileStream GetDataStream(int index)
        {
            FileStream stream;
            if (DataStreams.TryGetValue(index, out stream))
                return stream;

            string dataFile = Path.Combine(config.BasePath, String.Format("Data\\data\\data.{0:D3}", index));

            stream = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            DataStreams[index] = stream;

            return stream;
        }

        public static CASCHandler OpenLocalStorage(string basePath, BackgroundWorker worker = null)
        {
            CASCConfig config = CASCConfig.LoadLocalStorageConfig(basePath);

            return Open(worker, config);
        }

        private static CASCHandler Open(BackgroundWorker worker, CASCConfig config)
        {
            var cdn = CDNHandler.Initialize(config);
            return new CASCHandler(config, cdn, worker);
        }

        public CASCFolder LoadListFile(List<string> list)
        {
            var rootHash = Hasher.ComputeHash("root");

            var root = new CASCFolder(rootHash);

            CASCFolder.FolderNames[rootHash] = "root";

            foreach(var file in list)
            {

                CASCFolder folder = root;

                {
                    ulong fileHash = Hasher.ComputeHash(file);

                    // skip invalid names
                    if (!RootData.ContainsKey(fileHash))
                    {
                        Logger.WriteLine("Invalid file name: {0}", file);
                        continue;
                    }

                    string[] parts = file.Split('\\');

                    for (int i = 0; i < parts.Length; ++i)
                    {
                        bool isFile = (i == parts.Length - 1);

                        ulong hash = isFile ? fileHash : Hasher.ComputeHash(parts[i]);

                        ICASCEntry entry = folder.GetEntry(hash);

                        if (entry == null)
                        {
                            if (isFile)
                            {
                                entry = new CASCFile(hash);
                                CASCFile.FileNames[hash] = file;
                            }
                            else
                            {
                                entry = new CASCFolder(hash);
                                CASCFolder.FolderNames[hash] = parts[i];
                            }

                            folder.SubEntries[hash] = entry;

                            if (isFile)
                            {
                                folder = root;
                                break;
                            }
                        }

                        folder = entry as CASCFolder;
                    }
                }


                Logger.WriteLine("Found {0} ClientDB files.", CASCFile.FileNames.Count);
            }
            return root;
        }

        public bool FileExist(string file)
        {
            var hash = Hasher.ComputeHash(file);
            var rootInfos = GetRootInfo(hash);
            return rootInfos != null && rootInfos.Count > 0;
        }

        public Stream OpenFile(string file, LocaleFlags locale)
        {
            var hash = Hasher.ComputeHash(file);
            var rootInfos = GetRootInfo(hash);

            foreach (var rootInfo in rootInfos)
            {
                if ((rootInfo.Block.Flags & locale) != 0)
                {
                    var encInfo = GetEncodingInfo(rootInfo.MD5);

                    if (encInfo == null)
                        continue;

                    foreach (var key in encInfo.Keys)
                        return OpenFile(key);
                }
            }

            throw new NotSupportedException();
        }

        public MemoryStream SaveFileTo(string fullName, string extractPath, LocaleFlags locale)
        {
            var hash = Hasher.ComputeHash(fullName);
            var rootInfos = GetRootInfo(hash);

            foreach (var rootInfo in rootInfos)
            {
                if ((rootInfo.Block.Flags & locale) != 0)
                {
                    var encInfo = GetEncodingInfo(rootInfo.MD5);

                    if (encInfo == null)
                        continue;

                    foreach (var key in encInfo.Keys)
                    {
                        return ExtractFile(key, extractPath, fullName);
                    }
                }
            }

            return null;
        }
    }
}
