﻿/******************************************************
 *     ROMVault2 is written by Gordon J.              *
 *     Contact gordon@romvault.com                    *
 *     Copyright 2014                                 *
 ******************************************************/

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Compress;
using Compress.SevenZip;
using Compress.ThreadReaders;
using Compress.ZipFile;
using Compress.ZipFile.ZLib;
using ROMVault2.RvDB;
using ROMVault2.Utils;
using File = RVIO.File;
using FileInfo = RVIO.FileInfo;
using FileStream = RVIO.FileStream;
using Path = RVIO.Path;

namespace ROMVault2
{
    public enum ReturnCode
    {
        Good,
        RescanNeeded,
        FindFixes,
        LogicError,
        FileSystemError,
        SourceCRCCheckSumError,
        SourceCheckSumError,
        DestinationCheckSumError,
        StartOver
    }


    public static class FixFileCopy
    {
        private const int BufferSize = 4096*128;
        private static byte[] _buffer;

        // This Function returns:
        // Good            : Everything Worked Correctly
        // RescanNeeded     : Something unexpectidly changed in the files, so Stop prompt user to rescan.
        // LogicError       : This Should never happen and is a logic problem in the code
        // FileSystemError  : Something is wrong with the files


        /// <summary>
        ///     Performs the RomVault File Copy, with the source and destination being files or zipped files
        /// </summary>
        /// <param name="fileIn">This is the file being copied, it may be a zipped file or a regular file system file</param>
        /// <param name="zipFileOut">
        ///     This is the zip file that is being writen to, if it is null a new zip file will be made if we
        ///     are coping to a zip
        /// </param>
        /// <param name="zipFilenameOut">This is the name of the .zip file to be made that will be created in zipFileOut</param>
        /// <param name="fileOut">This is the actual output filename</param>
        /// <param name="forceRaw">if true then we will do a raw copy, this is so that we can copy corrupt zips</param>
        /// <param name="error">This is the returned error message if this copy fails</param>
        /// <param name="foundFile">
        ///     If we are SHA1/MD5 checking the source file for the first time, and it is different from what
        ///     we expected the correct values for this file are returned in foundFile
        /// </param>
        /// <returns>ReturnCode.Good is the valid return code otherwire we have an error</returns>
        public static ReturnCode CopyFile(RvFile fileIn, ref ICompress zipFileOut, string zipFilenameOut, RvFile fileOut, bool forceRaw, out string error, out RvFile foundFile)
        {
            foundFile = null;
            error = "";

            if (_buffer == null)
            {
                _buffer = new byte[BufferSize];
            }

            bool rawCopy = RawCopy(fileIn, fileOut, forceRaw);

            ulong streamSize = 0;
            ushort compressionMethod = 8;
            bool sourceTrrntzip = false;

            ICompress zipFileIn = null;
            Stream readStream = null;
            ReturnCode retC;


            bool isZeroLengthFile = DBHelper.IsZeroLengthFile(fileOut);
            if (!isZeroLengthFile)
            {
                //check that the in and out file match
                retC = CheckInputAndOutputFile(fileIn, fileOut, out error);
                if (retC != ReturnCode.Good)
                {
                    return retC;
                }

                //Find and Check/Open Input Files
                retC = OpenInputStream(fileIn, rawCopy, out zipFileIn, out sourceTrrntzip, out readStream, out streamSize, out compressionMethod, out error);
                if (retC != ReturnCode.Good)
                {
                    return retC;
                }
            }
            else
            {
                sourceTrrntzip = true;
            }

            if (!rawCopy && ((Program.rvSettings.FixLevel == eFixLevel.TrrntZipLevel1) || (Program.rvSettings.FixLevel == eFixLevel.TrrntZipLevel2) || (Program.rvSettings.FixLevel == eFixLevel.TrrntZipLevel3)))
            {
                compressionMethod = 8;
            }

            //Find and Check/Open Output Files
            Stream writeStream;
            retC = OpenOutputStream(fileOut, fileIn, ref zipFileOut, zipFilenameOut, compressionMethod, rawCopy, sourceTrrntzip, out writeStream, out error);
            if (retC != ReturnCode.Good)
            {
                return retC;
            }

            byte[] bCRC;
            byte[] bMD5 = null;
            byte[] bSHA1 = null;
            if (!isZeroLengthFile)
            {
                #region Do Data Tranfer

                ThreadCRC tcrc32 = null;
                ThreadMD5 tmd5 = null;
                ThreadSHA1 tsha1 = null;

                if (!rawCopy)
                {
                    tcrc32 = new ThreadCRC();
                    tmd5 = new ThreadMD5();
                    tsha1 = new ThreadSHA1();
                }

                ulong sizetogo = streamSize;

                while (sizetogo > 0)
                {
                    int sizenow = sizetogo > BufferSize ? BufferSize : (int) sizetogo;

                    try
                    {
                        readStream.Read(_buffer, 0, sizenow);
                    }
                    catch (ZlibException)
                    {
                        if ((fileIn.FileType == FileType.ZipFile) && (zipFileIn != null))
                        {
                            ZipReturn zr = zipFileIn.ZipFileCloseReadStream();
                            if (zr != ZipReturn.ZipGood)
                            {
                                error = "Error Closing " + zr + " Stream :" + zipFileIn.Filename(fileIn.ReportIndex);
                                return ReturnCode.FileSystemError;
                            }
                            zipFileIn.ZipFileClose();
                        }
                        else
                        {
                            readStream.Close();
                        }

                        if (fileOut.FileType == FileType.ZipFile)
                        {
                            ZipReturn zr = zipFileOut.ZipFileCloseWriteStream(new byte[] {0, 0, 0, 0});
                            if (zr != ZipReturn.ZipGood)
                            {
                                error = "Error Closing Stream " + zr;
                                return ReturnCode.FileSystemError;
                            }
                            zipFileOut.ZipFileRollBack();
                        }
                        else
                        {
                            writeStream.Flush();
                            writeStream.Close();
                            File.Delete(zipFilenameOut);
                        }

                        error = "Error in Data Stream";
                        return ReturnCode.SourceCRCCheckSumError;
                    }
                    catch (Exception e)
                    {
                        error = "Error reading Source File " + e.Message;
                        return ReturnCode.FileSystemError;
                    }

                    if (!rawCopy)
                    {
                        tcrc32.Trigger(_buffer, sizenow);
                        tmd5.Trigger(_buffer, sizenow);
                        tsha1.Trigger(_buffer, sizenow);

                        tcrc32.Wait();
                        tmd5.Wait();
                        tsha1.Wait();
                    }
                    try
                    {
                        writeStream.Write(_buffer, 0, sizenow);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.Message);
                        error = "Error writing out file. " + Environment.NewLine + e.Message;
                        return ReturnCode.FileSystemError;
                    }
                    sizetogo = sizetogo - (ulong) sizenow;
                }
                writeStream.Flush();

                #endregion

                #region Collect Checksums

                // if we did a full copy then we just calculated all the checksums while doing the copy
                if (!rawCopy)
                {
                    tcrc32.Finish();
                    tmd5.Finish();
                    tsha1.Finish();

                    bCRC = tcrc32.Hash;
                    bMD5 = tmd5.Hash;
                    bSHA1 = tsha1.Hash;

                    tcrc32.Dispose();
                    tmd5.Dispose();
                    tsha1.Dispose();
                }
                // if we raw copied and the source file has been FileChecked then we can trust the checksums in the source file
                else
                {
                    bCRC = ArrByte.Copy(fileIn.CRC);
                    if (fileIn.FileStatusIs(FileStatus.MD5Verified))
                    {
                        bMD5 = ArrByte.Copy(fileIn.MD5);
                    }
                    if (fileIn.FileStatusIs(FileStatus.SHA1Verified))
                    {
                        bSHA1 = ArrByte.Copy(fileIn.SHA1);
                    }
                }

                #endregion

                #region close the ReadStream

                if (((fileIn.FileType == FileType.ZipFile) || (fileIn.FileType == FileType.SevenZipFile)) && (zipFileIn != null))
                {
                    ZipReturn zr = zipFileIn.ZipFileCloseReadStream();
                    if (zr != ZipReturn.ZipGood)
                    {
                        error = "Error Closing " + zr + " Stream :" + zipFileIn.Filename(fileIn.ReportIndex);
                        return ReturnCode.FileSystemError;
                    }
                    zipFileIn.ZipFileClose();
                }
                else
                {
                    readStream.Close();

                    //if (RVIO.File.Exists(tmpFilename))
                    //    RVIO.File.Delete(tmpFilename);
                }

                #endregion
            }
            else
            {
                // Zero Length File (Directory in a Zip)
                if (fileOut.FileType == FileType.ZipFile)
                {
                    zipFileOut.ZipFileAddDirectory();
                }
                bCRC = VarFix.CleanMD5SHA1("00000000", 8);
                bMD5 = VarFix.CleanMD5SHA1("d41d8cd98f00b204e9800998ecf8427e", 32);
                bSHA1 = VarFix.CleanMD5SHA1("da39a3ee5e6b4b0d3255bfef95601890afd80709", 40);
            }

            #region close the WriteStream

            if ((fileOut.FileType == FileType.ZipFile) || (fileOut.FileType == FileType.SevenZipFile))
            {
                ZipReturn zr = zipFileOut.ZipFileCloseWriteStream(bCRC);
                if (zr != ZipReturn.ZipGood)
                {
                    error = "Error Closing Stream " + zr;
                    return ReturnCode.FileSystemError;
                }
                fileOut.ZipFileIndex = zipFileOut.LocalFilesCount() - 1;
                fileOut.ZipFileHeaderPosition = zipFileOut.LocalHeader(fileOut.ZipFileIndex);
            }
            else
            {
                writeStream.Flush();
                writeStream.Close();
                FileInfo fi = new FileInfo(zipFilenameOut);
                fileOut.TimeStamp = fi.LastWriteTime;
            }

            #endregion

            if (!isZeroLengthFile)
            {
                if (!rawCopy)
                {
                    if (!ArrByte.bCompare(bCRC, fileIn.CRC))
                    {
                        fileIn.GotStatus = GotStatus.Corrupt;
                        error = "Source CRC does not match Source Data stream, corrupt Zip";

                        if (fileOut.FileType == FileType.ZipFile)
                        {
                            zipFileOut.ZipFileRollBack();
                        }
                        else
                        {
                            File.Delete(zipFilenameOut);
                        }

                        return ReturnCode.SourceCRCCheckSumError;
                    }

                    fileIn.FileStatusSet(FileStatus.CRCVerified | FileStatus.SizeVerified);

                    bool sourceFailed = false;

                    // check to see if we have a MD5 from the DAT file
                    if (fileIn.FileStatusIs(FileStatus.MD5FromDAT))
                    {
                        if (fileIn.MD5 == null)
                        {
                            error = "Should have an filein MD5 from Dat but not found. Logic Error.";
                            return ReturnCode.LogicError;
                        }

                        if (!ArrByte.bCompare(fileIn.MD5, bMD5))
                        {
                            sourceFailed = true;
                        }
                        else
                        {
                            fileIn.FileStatusSet(FileStatus.MD5Verified);
                        }
                    }
                    // check to see if we have an MD5 (not from the DAT) so must be from previously scanning this file.
                    else if (fileIn.MD5 != null)
                    {
                        if (!ArrByte.bCompare(fileIn.MD5, bMD5))
                        {
                            // if we had an MD5 from a preview scan and it now does not match, something has gone really bad.
                            error = "The MD5 found does not match a previously scanned MD5, this should not happen, unless something got corrupt.";
                            return ReturnCode.LogicError;
                        }
                    }
                    else // (FileIn.MD5 == null)
                    {
                        fileIn.MD5 = bMD5;
                        fileIn.FileStatusSet(FileStatus.MD5Verified);
                    }


                    // check to see if we have a SHA1 from the DAT file
                    if (fileIn.FileStatusIs(FileStatus.SHA1FromDAT))
                    {
                        if (fileIn.SHA1 == null)
                        {
                            error = "Should have an filein SHA1 from Dat but not found. Logic Error.";
                            return ReturnCode.LogicError;
                        }

                        if (!ArrByte.bCompare(fileIn.SHA1, bSHA1))
                        {
                            sourceFailed = true;
                        }
                        else
                        {
                            fileIn.FileStatusSet(FileStatus.SHA1Verified);
                        }
                    }
                    // check to see if we have an SHA1 (not from the DAT) so must be from previously scanning this file.
                    else if (fileIn.SHA1 != null)
                    {
                        if (!ArrByte.bCompare(fileIn.SHA1, bSHA1))
                        {
                            // if we had an SHA1 from a preview scan and it now does not match, something has gone really bad.
                            error = "The SHA1 found does not match a previously scanned SHA1, this should not happen, unless something got corrupt.";
                            return ReturnCode.LogicError;
                        }
                    }
                    else // (FileIn.SHA1 == null)
                    {
                        fileIn.SHA1 = bSHA1;
                        fileIn.FileStatusSet(FileStatus.SHA1Verified);
                    }


                    if (sourceFailed)
                    {
                        if ((fileIn.FileType == FileType.ZipFile) || (fileIn.FileType == FileType.SevenZipFile))
                        {
                            RvFile tZFile = new RvFile(FileType.ZipFile);
                            foundFile = tZFile;
                            tZFile.ZipFileIndex = fileIn.ZipFileIndex;
                            tZFile.ZipFileHeaderPosition = fileIn.ZipFileHeaderPosition;
                        }
                        else
                        {
                            foundFile = new RvFile(fileIn.FileType);
                        }

                        foundFile.Name = fileIn.Name;
                        foundFile.Size = fileIn.Size;
                        foundFile.CRC = bCRC;
                        foundFile.MD5 = bMD5;
                        foundFile.SHA1 = bSHA1;
                        foundFile.TimeStamp = fileIn.TimeStamp;
                        foundFile.SetStatus(DatStatus.NotInDat, GotStatus.Got);

                        foundFile.FileStatusSet(FileStatus.SizeVerified | FileStatus.CRCVerified | FileStatus.MD5Verified | FileStatus.SHA1Verified);

                        if (fileOut.FileType == FileType.ZipFile)
                        {
                            zipFileOut.ZipFileRollBack();
                        }
                        else
                        {
                            File.Delete(zipFilenameOut);
                        }

                        return ReturnCode.SourceCheckSumError;
                    }
                }
            }

            if ((fileOut.FileType == FileType.ZipFile) || (fileOut.FileType == FileType.SevenZipFile))
            {
                fileOut.FileStatusSet(FileStatus.SizeFromHeader | FileStatus.CRCFromHeader);
            }

            if (fileOut.FileStatusIs(FileStatus.CRCFromDAT) && (fileOut.CRC != null) && !ArrByte.bCompare(fileOut.CRC, bCRC))
            {
                //Rollback the file
                if (fileOut.FileType == FileType.ZipFile)
                {
                    zipFileOut.ZipFileRollBack();
                }
                else
                {
                    File.Delete(zipFilenameOut);
                }

                return ReturnCode.DestinationCheckSumError;
            }

            fileOut.CRC = bCRC;
            if (!rawCopy || fileIn.FileStatusIs(FileStatus.CRCVerified))
            {
                fileOut.FileStatusSet(FileStatus.CRCVerified);
            }


            if (bSHA1 != null)
            {
                if (fileOut.FileStatusIs(FileStatus.SHA1FromDAT) && (fileOut.SHA1 != null) && !ArrByte.bCompare(fileOut.SHA1, bSHA1))
                {
                    //Rollback the file
                    if (fileOut.FileType == FileType.ZipFile)
                    {
                        zipFileOut.ZipFileRollBack();
                    }
                    else
                    {
                        File.Delete(zipFilenameOut);
                    }

                    return ReturnCode.DestinationCheckSumError;
                }

                fileOut.SHA1 = bSHA1;
                fileOut.FileStatusSet(FileStatus.SHA1Verified);
            }

            if (bMD5 != null)
            {
                if (fileOut.FileStatusIs(FileStatus.MD5FromDAT) && (fileOut.MD5 != null) && !ArrByte.bCompare(fileOut.MD5, bMD5))
                {
                    //Rollback the file
                    if (fileOut.FileType == FileType.ZipFile)
                    {
                        zipFileOut.ZipFileRollBack();
                    }
                    else
                    {
                        File.Delete(zipFilenameOut);
                    }

                    return ReturnCode.DestinationCheckSumError;
                }
                fileOut.MD5 = bMD5;
                fileOut.FileStatusSet(FileStatus.MD5Verified);
            }

            if (fileIn.Size != null)
            {
                fileOut.Size = fileIn.Size;
                fileOut.FileStatusSet(FileStatus.SizeVerified);
            }


            if (fileIn.GotStatus == GotStatus.Corrupt)
            {
                fileOut.GotStatus = GotStatus.Corrupt;
            }
            else
            {
                fileOut.GotStatus = GotStatus.Got; // Changes RepStatus to Correct
            }

            fileOut.FileStatusSet(FileStatus.SizeVerified);

            if ((fileOut.SHA1CHD == null) && (fileIn.SHA1CHD != null))
            {
                fileOut.SHA1CHD = fileIn.SHA1CHD;
            }
            if ((fileOut.MD5CHD == null) && (fileIn.MD5CHD != null))
            {
                fileOut.MD5CHD = fileIn.MD5CHD;
            }


            fileOut.CHDVersion = fileIn.CHDVersion;

            fileOut.FileStatusSet(FileStatus.SHA1CHDFromHeader | FileStatus.MD5CHDFromHeader | FileStatus.SHA1CHDVerified | FileStatus.MD5CHDVerified, fileIn);


            return ReturnCode.Good;
        }

        //Raw Copy
        // Returns True is a raw copy can be used
        // Returns False is a full recompression is required

        private static bool RawCopy(RvFile fileIn, RvFile fileOut, bool forceRaw)
        {
            if ((fileIn == null) || (fileOut == null))
            {
                return false;
            }

            if ((fileIn.FileType != FileType.ZipFile) || (fileOut.FileType != FileType.ZipFile))
            {
                return false;
            }

            if (fileIn.Parent == null)
            {
                return false;
            }

            if (forceRaw)
            {
                return true;
            }

            bool trrntzipped = (fileIn.Parent.ZipStatus & ZipStatus.TrrntZip) == ZipStatus.TrrntZip;

            bool deepchecked = fileIn.FileStatusIs(FileStatus.SHA1Verified) && fileIn.FileStatusIs(FileStatus.MD5Verified);

            switch (Program.rvSettings.FixLevel)
            {
                case eFixLevel.TrrntZipLevel1:
                    return trrntzipped;
                case eFixLevel.TrrntZipLevel2:
                    return trrntzipped && deepchecked;
                case eFixLevel.TrrntZipLevel3:
                    return false;

                case eFixLevel.Level1:
                    return true;
                case eFixLevel.Level2:
                    return deepchecked;
                case eFixLevel.Level3:
                    return false;
            }

            return false;
        }

        private static ReturnCode CheckInputAndOutputFile(RvFile fileIn, RvFile fileOut, out string error)
        {
            if (fileOut.FileStatusIs(FileStatus.SizeFromDAT) && (fileOut.Size != null) && (fileIn.Size != fileOut.Size))
            {
                error = "Source and destination Size does not match. Logic Error.";
                return ReturnCode.LogicError;
            }
            if (fileOut.FileStatusIs(FileStatus.CRCFromDAT) && (fileOut.CRC != null) && !ArrByte.bCompare(fileIn.CRC, fileOut.CRC))
            {
                error = "Source and destination CRC does not match. Logic Error.";
                return ReturnCode.LogicError;
            }

            if (fileOut.FileStatusIs(FileStatus.SHA1FromDAT) && fileIn.FileStatusIs(FileStatus.SHA1Verified))
            {
                if ((fileIn.SHA1 != null) && (fileOut.SHA1 != null) && !ArrByte.bCompare(fileIn.SHA1, fileOut.SHA1))
                {
                    error = "Source and destination SHA1 does not match. Logic Error.";
                    return ReturnCode.LogicError;
                }
            }
            if (fileOut.FileStatusIs(FileStatus.MD5CHDFromDAT) && fileIn.FileStatusIs(FileStatus.MD5Verified))
            {
                if ((fileIn.MD5 != null) && (fileOut.MD5 != null) && !ArrByte.bCompare(fileIn.MD5, fileOut.MD5))
                {
                    error = "Source and destination SHA1 does not match. Logic Error.";
                    return ReturnCode.LogicError;
                }
            }
            error = "";
            return ReturnCode.Good;
        }

        private static ReturnCode OpenInputStream(RvFile fileIn, bool rawCopy, out ICompress zipFileIn, out bool sourceTrrntzip, out Stream readStream, out ulong streamSize, out ushort compressionMethod, out string error)
        {
            zipFileIn = null;
            sourceTrrntzip = false;
            readStream = null;
            streamSize = 0;
            compressionMethod = 0;

            if ((fileIn.FileType == FileType.ZipFile) || (fileIn.FileType == FileType.SevenZipFile)) // Input is a ZipFile
            {
                RvDir zZipFileIn = fileIn.Parent;
                if (zZipFileIn.FileType != DBTypeGet.DirFromFile(fileIn.FileType))
                {
                    error = "File Open but Source File is not correct type, Logic Error.";
                    return ReturnCode.LogicError;
                }

                string fileNameIn = zZipFileIn.FullName;
                ZipReturn zr1;


                if (zZipFileIn.FileType == FileType.SevenZip)
                {
                    sourceTrrntzip = false;
                    zipFileIn = new SevenZ();
                    zr1 = zipFileIn.ZipFileOpen(fileNameIn, zZipFileIn.TimeStamp, true);
                }
                else
                {
                    sourceTrrntzip = (zZipFileIn.ZipStatus & ZipStatus.TrrntZip) == ZipStatus.TrrntZip;
                    zipFileIn = new ZipFile();
                    zr1 = zipFileIn.ZipFileOpen(fileNameIn, zZipFileIn.TimeStamp, fileIn.ZipFileHeaderPosition == null);
                }

                switch (zr1)
                {
                    case ZipReturn.ZipGood:
                        break;
                    case ZipReturn.ZipErrorFileNotFound:
                        error = "File not found, Rescan required before fixing " + fileIn.Name;
                        return ReturnCode.FileSystemError;
                    case ZipReturn.ZipErrorTimeStamp:
                        error = "File has changed, Rescan required before fixing " + fileIn.Name;
                        return ReturnCode.FileSystemError;
                    default:
                        error = "Error Open Zip" + zr1 + ", with File " + fileIn.DatFullName;
                        return ReturnCode.FileSystemError;
                }

                if (fileIn.FileType == FileType.SevenZipFile)
                {
                    ((SevenZ) zipFileIn).ZipFileOpenReadStream(fileIn.ZipFileIndex, out readStream, out streamSize);
                }
                else
                {
                    if (fileIn.ZipFileHeaderPosition != null)
                    {
                        ((ZipFile) zipFileIn).ZipFileOpenReadStreamQuick((ulong) fileIn.ZipFileHeaderPosition, rawCopy, out readStream, out streamSize, out compressionMethod);
                    }
                    else
                    {
                        ((ZipFile) zipFileIn).ZipFileOpenReadStream(fileIn.ZipFileIndex, rawCopy, out readStream, out streamSize, out compressionMethod);
                    }
                }
            }
            else // Input is a regular file
            {
                string fileNameIn = fileIn.FullName;
                if (!File.Exists(fileNameIn))
                {
                    error = "Rescan needed, File Changed :" + fileNameIn;
                    return ReturnCode.RescanNeeded;
                }
                FileInfo fileInInfo = new FileInfo(fileNameIn);
                if (fileInInfo.LastWriteTime != fileIn.TimeStamp)
                {
                    error = "Rescan needed, File Changed :" + fileNameIn;
                    return ReturnCode.RescanNeeded;
                }
                int errorCode = FileStream.OpenFileRead(fileNameIn, out readStream);
                if (errorCode != 0)
                {
                    error = new Win32Exception(errorCode).Message + ". " + fileNameIn;
                    return ReturnCode.FileSystemError;
                }
                if (fileIn.Size == null)
                {
                    error = "Null File Size found in Fixing File :" + fileNameIn;
                    return ReturnCode.LogicError;
                }
                streamSize = (ulong) fileIn.Size;
                if ((ulong) readStream.Length != streamSize)
                {
                    error = "Rescan needed, File Length Changed :" + fileNameIn;
                    return ReturnCode.RescanNeeded;
                }
            }

            error = "";
            return ReturnCode.Good;
        }

        private static ReturnCode OpenOutputStream(RvFile fileOut, RvFile fileIn, ref ICompress zipFileOut, string zipFilenameOut, ushort compressionMethod, bool rawCopy, bool sourceTrrntzip, out Stream writeStream, out string error)
        {
            writeStream = null;

            if ((fileOut.FileType == FileType.ZipFile) || (fileOut.FileType == FileType.SevenZipFile))
            {
                // if ZipFileOut == null then we have not open the output zip yet so open it from writing.
                if (zipFileOut == null)
                {
                    if (Path.GetFileName(zipFilenameOut) == "__RomVault.tmp")
                    {
                        if (File.Exists(zipFilenameOut))
                        {
                            File.Delete(zipFilenameOut);
                        }
                    }
                    else if (File.Exists(zipFilenameOut))
                    {
                        error = "Rescan needed, File Changed :" + zipFilenameOut;
                        return ReturnCode.RescanNeeded;
                    }

                    if (fileOut.FileType == FileType.ZipFile)
                    {
                        zipFileOut = new ZipFile();
                    }
                    else
                    {
                        zipFileOut = new SevenZ();
                    }

                    ZipReturn zrf = zipFileOut.ZipFileCreate(zipFilenameOut);
                    if (zrf != ZipReturn.ZipGood)
                    {
                        error = "Error Opening Write Stream " + zrf;
                        return ReturnCode.FileSystemError;
                    }
                }
                else
                {
                    if (zipFileOut.ZipOpen != ZipOpenType.OpenWrite)
                    {
                        error = "Output Zip File is not set to OpenWrite, Logic Error.";
                        return ReturnCode.LogicError;
                    }

                    if (zipFileOut.ZipFilename != new FileInfo(zipFilenameOut).FullName)
                    {
                        error = "Output Zip file has changed name from " + zipFileOut.ZipFilename + " to " + zipFilenameOut + ". Logic Error";
                        return ReturnCode.LogicError;
                    }
                }

                if (fileIn.Size == null)
                {
                    error = "Null File Size found in Fixing File :" + fileIn.FullName;
                    return ReturnCode.LogicError;
                }
                ZipReturn zr = zipFileOut.ZipFileOpenWriteStream(rawCopy, sourceTrrntzip, fileOut.Name, (ulong) fileIn.Size, compressionMethod, out writeStream);
                if (zr != ZipReturn.ZipGood)
                {
                    error = "Error Opening Write Stream " + zr;
                    return ReturnCode.FileSystemError;
                }
            }
            else
            {
                if (File.Exists(zipFilenameOut) && (fileOut.GotStatus != GotStatus.Corrupt))
                {
                    error = "Rescan needed, File Changed :" + zipFilenameOut;
                    return ReturnCode.RescanNeeded;
                }
                int errorCode = FileStream.OpenFileWrite(zipFilenameOut, out writeStream);
                if (errorCode != 0)
                {
                    error = new Win32Exception(errorCode).Message + ". " + zipFilenameOut;
                    return ReturnCode.FileSystemError;
                }
            }


            error = "";
            return ReturnCode.Good;
        }
    }
}