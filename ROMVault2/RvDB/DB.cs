﻿/******************************************************
 *     ROMVault2 is written by Gordon J.              *
 *     Contact gordon@romvault.com                    *
 *     Copyright 2014                                 *
 ******************************************************/

using System.ComponentModel;
using System.IO;
using System.Threading;
using ROMVault2.Properties;
using File = RVIO.File;
using FileStream = System.IO.FileStream;

namespace ROMVault2.RvDB
{
    public static class DBVersion
    {
        public const int Version = 7;
        public static int VersionNow;
    }

    public static class DB
    {
        private const ulong EndCacheMarker = 0x15a600dda7;

        public static BackgroundWorker Bgw;

        public static RvDir DirTree;

        private static void OpenDefaultDB()
        {
            DirTree = new RvDir(FileType.Dir)
            {
                Tree = new RvTreeRow(),
                DatStatus = DatStatus.InDatCollect
            };

            RvDir rv = new RvDir(FileType.Dir)
            {
                Name = "RomVault",
                Tree = new RvTreeRow(),
                DatStatus = DatStatus.InDatCollect
            };
            DirTree.ChildAdd(rv);

            RvDir ts = new RvDir(FileType.Dir)
            {
                Name = "ToSort",
                Tree = new RvTreeRow(),
                DatStatus = DatStatus.InDatCollect
            };
            DirTree.ChildAdd(ts);
        }

        public static void Write()
        {
            if (File.Exists(Program.rvSettings.CacheFile))
            {
                string bname = Program.rvSettings.CacheFile + "Backup";
                if (File.Exists(bname))
                {
                    File.Delete(bname);
                }
                File.Move(Program.rvSettings.CacheFile, bname);
            }
            FileStream fs = new FileStream(Program.rvSettings.CacheFile, FileMode.CreateNew, FileAccess.Write);
            BinaryWriter bw = new BinaryWriter(fs);
            DBVersion.VersionNow = DBVersion.Version;
            bw.Write(DBVersion.Version);
            DirTree.Write(bw);

            bw.Write(EndCacheMarker);

            bw.Flush();
            bw.Close();

            fs.Close();
            fs.Dispose();
        }

        public static void Read(object sender, DoWorkEventArgs e)
        {
            Bgw = sender as BackgroundWorker;
            Program.SyncCont = e.Argument as SynchronizationContext;

            if (!File.Exists(Program.rvSettings.CacheFile))
            {
                OpenDefaultDB();
                Bgw = null;
                Program.SyncCont = null;
                return;
            }
            DirTree = new RvDir(FileType.Dir);
            FileStream fs = new FileStream(Program.rvSettings.CacheFile, FileMode.Open, FileAccess.Read);
            if (fs.Length < 4)
            {
                ReportError.UnhandledExceptionHandler("Cache is Corrupt, revert to Backup.");
            }


            BinaryReader br = new BinaryReader(fs);

            Bgw?.ReportProgress(0, new bgwSetRange((int) fs.Length));

            DBVersion.VersionNow = br.ReadInt32();

            if (DBVersion.VersionNow != DBVersion.Version)
            {
                ReportError.Show(Resources.DB_Read_Data_Cache_version_is_out_of_date_you_should_now_rescan_your_dat_directory_and_roms_directory_);
                OpenDefaultDB();
            }
            else
            {
                DirTree.Read(br, null);
            }

            if (fs.Position > fs.Length - 8)
            {
                ReportError.UnhandledExceptionHandler("Cache is Corrupt, revert to Backup.");
            }

            ulong testEOF = br.ReadUInt64();
            if (testEOF != EndCacheMarker)
            {
                ReportError.UnhandledExceptionHandler("Cache is Corrupt, revert to Backup.");
            }

            br.Close();
            fs.Close();
            fs.Dispose();

            Bgw = null;
            Program.SyncCont = null;
        }

        public static string Fn(string v)
        {
            return v ?? "";
        }
    }
}