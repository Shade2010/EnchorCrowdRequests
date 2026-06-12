using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EnchorCrowdRequests
{
    // Decodes an enchor.us .sng container into a Clone Hero song folder (the
    // universally-compatible format). The .sng holds the song.ini values in its
    // metadata section and the chart/audio/art as masked files.
    //
    // Format (little-endian), per github.com/mdsitton/SngFileFormat:
    //   header: "SNGPKG"(6) | version u32 | xorMask[16]
    //   metadata: metadataLen u64 | count u64 | { keyLen i32, key, valLen i32, val } * count
    //   fileIndex: fileMetaLen u64 | count u64 | { nameLen u8, name, contentsLen u64, contentsIndex u64 } * count
    //   file data: at each contentsIndex, contentsLen masked bytes
    //   unmask: out[i] = in[i] ^ (xorMask[i % 16] ^ (i & 0xFF)), i relative to each file's start
    public static class SngExtractor
    {
        private struct FileEntry { public string Name; public long Len; public long Index; }

        // Pure-BCL so it can be unit-tested outside the game.
        public static void ExtractToFolder(byte[] data, string folder)
        {
            if (data == null || data.Length < 26 ||
                data[0] != 'S' || data[1] != 'N' || data[2] != 'G' ||
                data[3] != 'P' || data[4] != 'K' || data[5] != 'G')
                throw new InvalidDataException("Not an SNG file (bad magic).");

            int pos = 6;
            ReadU32(data, ref pos); // version (unused)
            var mask = new byte[16];
            Array.Copy(data, pos, mask, 0, 16);
            pos += 16;

            // Metadata -> song.ini
            ReadU64(data, ref pos);                 // metadataLen (unused)
            long metaCount = (long)ReadU64(data, ref pos);
            var meta = new List<KeyValuePair<string, string>>();
            for (long i = 0; i < metaCount; i++)
            {
                int keyLen = ReadI32(data, ref pos);
                string key = Encoding.UTF8.GetString(data, pos, keyLen); pos += keyLen;
                int valLen = ReadI32(data, ref pos);
                string val = Encoding.UTF8.GetString(data, pos, valLen); pos += valLen;
                meta.Add(new KeyValuePair<string, string>(key, val));
            }

            // File index
            ReadU64(data, ref pos);                 // fileMetaLen (unused)
            long fileCount = (long)ReadU64(data, ref pos);
            var files = new List<FileEntry>();
            for (long i = 0; i < fileCount; i++)
            {
                int nameLen = data[pos]; pos += 1;
                string name = Encoding.UTF8.GetString(data, pos, nameLen); pos += nameLen;
                long clen = (long)ReadU64(data, ref pos);
                long cidx = (long)ReadU64(data, ref pos);
                files.Add(new FileEntry { Name = name, Len = clen, Index = cidx });
            }

            Directory.CreateDirectory(folder);

            if (meta.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("[Song]\n");
                foreach (var kv in meta)
                    sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
                File.WriteAllText(Path.Combine(folder, "song.ini"), sb.ToString(), new UTF8Encoding(false));
            }

            foreach (FileEntry f in files)
            {
                if (f.Index < 0 || f.Len < 0 || f.Index + f.Len > data.Length)
                    throw new InvalidDataException("SNG file entry out of range: " + f.Name);

                var outBytes = new byte[f.Len];
                long start = f.Index;
                for (long i = 0; i < f.Len; i++)
                {
                    byte key = (byte)(mask[i % 16] ^ (i & 0xFF));
                    outBytes[i] = (byte)(data[start + i] ^ key);
                }

                string rel = SanitizeRelative(f.Name);
                string outPath = Path.Combine(folder, rel);
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outPath, outBytes);
            }
        }

        private static string SanitizeRelative(string name)
        {
            string[] parts = name.Replace('\\', '/').Split('/');
            var clean = new List<string>();
            foreach (string p in parts)
            {
                if (p.Length == 0 || p == "." || p == "..") continue; // no traversal
                clean.Add(SongPath.Sanitize(p));
            }
            return clean.Count == 0 ? "_" : string.Join(Path.DirectorySeparatorChar.ToString(), clean);
        }

        private static uint ReadU32(byte[] d, ref int p) { uint v = BitConverter.ToUInt32(d, p); p += 4; return v; }
        private static int ReadI32(byte[] d, ref int p) { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
        private static ulong ReadU64(byte[] d, ref int p) { ulong v = BitConverter.ToUInt64(d, p); p += 8; return v; }
    }
}

