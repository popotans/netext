// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel; // For Win32Excption;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Dia2Lib;
using Address = System.UInt64;
using System.Reflection;
using Microsoft.Diagnostics.Runtime.Utilities;
using Microsoft.Diagnostics.Runtime;

#pragma warning disable 649

namespace Microsoft.Diagnostics.Runtime.Utilities
{
    /// <summary>
    /// A symbol reader represents something that can FIND pdbs (either on a symbol server or via a symbol path)
    /// Its job is to find a full path a PDB.  Then you can use OpenSymbolFile to get a SymbolReaderModule and do more. 
    /// </summary>
    internal sealed unsafe class SymbolReader
    {
        /// <summary>
        /// Opens a new SymbolReader.   All diagnostics messages about symbol lookup go to 'log'.  
        /// </summary>
        public SymbolReader(TextWriter log, SymPath nt_symbol_path)
        {
            SymbolPath = nt_symbol_path;
            log.WriteLine("Created SymbolReader with SymbolPath {0}", nt_symbol_path);

            // TODO FIX NOW.  the code below does not support probing a file extension directory.  
            // we work around this by adding more things to the symbol path
            var newSymPath = new SymPath();
            foreach (var symElem in SymbolPath.Elements)
            {
                newSymPath.Add(symElem);
                if (!symElem.IsSymServer)
                {
                    var probe = Path.Combine(symElem.Target, "dll");
                    if (Directory.Exists(probe))
                        newSymPath.Add(probe);
                    probe = Path.Combine(symElem.Target, "exe");
                    if (Directory.Exists(probe))
                        newSymPath.Add(probe);
                }
            }
            var newSymPathStr = newSymPath.ToString();
            // log.WriteLine("Morphed Symbol Path: {0}", newSymPathStr);

            this._log = log;

            SymbolReaderNativeMethods.SymOptions options = SymbolReaderNativeMethods.SymGetOptions();
            SymbolReaderNativeMethods.SymSetOptions(
            SymbolReaderNativeMethods.SymOptions.SYMOPT_DEBUG |
            // SymbolReaderNativeMethods.SymOptions.SYMOPT_DEFERRED_LOADS |
            SymbolReaderNativeMethods.SymOptions.SYMOPT_LOAD_LINES |
            SymbolReaderNativeMethods.SymOptions.SYMOPT_EXACT_SYMBOLS |
            SymbolReaderNativeMethods.SymOptions.SYMOPT_UNDNAME
            );

            if (newSymPathStr != s_symPath)
                StaticInit(newSymPathStr);
        }

        static SymbolReader()
        {
            s_currentProcess = Process.GetCurrentProcess();  // Only here to insure processHandle does not die.  TODO get on safeHandles. 
            s_currentProcessHandle = s_currentProcess.Handle;
            s_callback = new SymbolReaderNativeMethods.SymRegisterCallbackProc(StatusCallback);
        }

        private static void StaticInit(string newSymPathStr)
        {
            s_symPath = newSymPathStr;
            bool success = SymbolReaderNativeMethods.SymInitializeW(s_currentProcessHandle, newSymPathStr, false);
            if (!success)
            {
                // This captures the GetLastEvent (and has to happen before calling CloseHandle()
                s_currentProcessHandle = IntPtr.Zero;
                throw new Win32Exception();
            }

            success = SymbolReaderNativeMethods.SymRegisterCallbackW64(s_currentProcessHandle, s_callback, 0);
            Debug.Assert(success);
        }
        /// <summary>
        /// Find the complete PDB path, given just the simple name (filename + pdb extension) as well as its 'signature', 
        /// which uniquely identifies it (on symbol servers).   Uses the SymbolReader.SymbolPath (including Symbol servers) to 
        /// look up the PDB, and will download the PDB to the local cache if necessary.  
        /// 
        /// A Guid of 0, means 'unknown' and will match the first PDB that matches simple name.  Thus it is unsafe. 
        /// 
        /// Returns null if the PDB could  not be found
        /// 
        /// </summary>
        /// <param name="pdbSimpleName">The name of the PDB file (we only use the file name part)</param>
        /// <param name="pdbIndexGuid">The GUID that is embedded in the DLL in the debug information that allows matching the DLL and the PDB</param>
        /// <param name="pdbIndexAge">Tools like BBT transform a DLL into another DLL (with the same GUID) the 'pdbAge' is a small integers
        /// that indicates how many transformations were done</param>
        /// <param name="dllFilePath">If you know the path to the DLL for this pdb add it here.  That way we can probe next to the DLL
        /// for the PDB file.</param>
        /// <param name="fileVersion">This is an optional string that identifies the file version (the 'Version' resource information.  
        /// It is used only to provided better error messages for the log.</param>
        public string FindSymbolFilePath(string pdbSimpleName, Guid pdbIndexGuid, int pdbIndexAge,
            string dllFilePath = null, string fileVersion = "")
        {
            SymbolReaderNativeMethods.SymFindFileInPathProc FindSymbolFileCallBack = delegate (string fileName, IntPtr context)
            {
                Debug.Assert(context == IntPtr.Zero);
                Guid fileGuid = Guid.Empty;
                int fileAge = 0;
                int dummy = 0;
                if (!SymbolReaderNativeMethods.SymSrvGetFileIndexesW(fileName, ref fileGuid, ref fileAge, ref dummy, 0))
                {
                    _log.WriteLine("Failed to look up PDB signature for {0}.", fileName);
                    return true;        // continue searching.  
                }
                bool matched = (pdbIndexGuid == fileGuid && pdbIndexAge == fileAge);
                if (!matched)
                {
                    if (pdbIndexGuid == Guid.Empty)
                    {
                        _log.WriteLine("No PDB Guid provided, assuming an unsafe PDB match for {0}", fileName);
                        matched = true;
                    }
                    else
                        _log.WriteLine("PDB File {0} has Guid {1} age {2} != Desired Guid {3} age {4}",
                            fileName, fileGuid, fileAge, pdbIndexGuid, pdbIndexAge);
                }
                return !matched;        // you return false when you match, true to continue searching
            };

            StringBuilder pdbFullPath = new StringBuilder(260);
            bool foundPDB = SymbolReaderNativeMethods.SymFindFileInPathW(s_currentProcessHandle,
                null,                  // Search path
                pdbSimpleName,
                ref pdbIndexGuid,          // ID (&GUID)
                pdbIndexAge,               // ID 2
                0,                    // ID 3
                SymbolReaderNativeMethods.SSRVOPT_GUIDPTR,  // Flags
                pdbFullPath,    // output FilePath
                FindSymbolFileCallBack,
                IntPtr.Zero);   // Context for callback 
            string pdbPath = null;
            if (foundPDB)
            {
                pdbPath = pdbFullPath.ToString();
                goto Success;
            }

            // TODO is ONLY looking in the cache the right policy?   Hmmm...
            if ((Flags & SymbolReaderFlags.CacheOnly) == 0)
            {
                // We check these last because they may be hostile PDBs and we have to ask the user about them.
                if (dllFilePath != null)        // Check next to the file. 
                {
                    string pdbPathCandidate = Path.ChangeExtension(dllFilePath, ".pdb");

                    // Also try the symbols.pri\retail\dll convention that windows and devdiv use
                    if (!File.Exists(pdbPathCandidate))
                        pdbPathCandidate = Path.Combine(
                            Path.GetDirectoryName(dllFilePath), @"symbols.pri\retail\dll\" +
                            Path.GetFileNameWithoutExtension(dllFilePath) + ".pdb");

                    if (File.Exists(pdbPathCandidate))
                    {
                        if (!FindSymbolFileCallBack(pdbPathCandidate, IntPtr.Zero))
                        {
                            if (CheckSecurity(pdbPathCandidate))
                            {
                                pdbPath = pdbPathCandidate;
                                goto Success;
                            }
                        }
                    }
                }

                // If the pdbPath is a full path, see if it exists 
                if (pdbSimpleName.IndexOf('\\') > 0 && File.Exists(pdbSimpleName))
                {
                    if (!FindSymbolFileCallBack(pdbSimpleName, IntPtr.Zero))
                    {
                        if (CheckSecurity(pdbSimpleName))
                        {
                            pdbPath = pdbSimpleName;
                            goto Success;
                        }
                    }
                }
            }

            string where = "";
            if ((Flags & SymbolReaderFlags.CacheOnly) != 0)
                where = " in local cache";
            _log.WriteLine("Failed to find PDB {0}{1}.\r\n    GUID {2} Age {3} Version {4}",
                pdbSimpleName, where, pdbIndexGuid, pdbIndexAge, fileVersion);
            return null;
        Success:
            _log.WriteLine("Successfully found PDB {0}\r\n    GUID {1} Age {2} Version {3}", pdbPath, pdbIndexGuid, pdbIndexAge, fileVersion);
            // If the PDB is on a network share, copy it to the local sym
            return CacheFileLocally(pdbPath, pdbIndexGuid, pdbIndexAge);
        }

        public string FindSymbolFilePath(string pdbSimpleName, Guid pdbIndexGuid, int pdbIndexAge, ISymbolNotification notification)
        {
            string pdbIndexPath = null;
            foreach (SymPathElement element in SymbolPath.Elements)
            {
                if (element.IsSymServer)
                {
                    pdbSimpleName = Path.GetFileName(pdbSimpleName);
                    if (pdbIndexPath == null)
                        pdbIndexPath = pdbSimpleName + @"\" + pdbIndexGuid.ToString().Replace("-", "") + pdbIndexAge.ToString("x") + @"\" + pdbSimpleName;

                    string cache = element.Cache;
                    if (cache == null)
                        cache = SymbolPath.DefaultSymbolCache;

                    string targetPath = GetFileFromServer(element.Target, pdbIndexPath, cache, notification);
                    if (targetPath != null)
                        return targetPath;
                }
                else
                {
                    string filePath = Path.Combine(element.Target, pdbSimpleName);
                    _log.WriteLine("Probing file {0}", filePath);
                    if (File.Exists(filePath))
                    {
                        using (PEFile file = new PEFile(filePath))
                        {
                            IDiaDataSource source = DiaLoader.GetDiaSourceObject();
                            IDiaSession session;
                            source.loadDataFromPdb(filePath);
                            source.openSession(out session);

                            if (pdbIndexGuid == session.globalScope.guid)
                            {
                                notification.FoundSymbolOnPath(filePath);
                                return filePath;
                            }

                            _log.WriteLine("Found file {0} but guid {1} != desired {2}, rejecting.", filePath, session.globalScope.guid, pdbIndexGuid);
                        }
                    }

                    notification.ProbeFailed(filePath);
                }
            }

            return null;
        }

        /// <summary>
        /// This API looks up an executable file, by its build-timestamp and size (on a symbol server),  'fileName' should be 
        /// a simple name (no directory), and you need the buildTimeStamp and sizeOfImage that are found in the PE header.
        /// 
        /// Returns null if it cannot find anything.  
        /// </summary>
        public string FindExecutableFilePath(string fileName, int buildTimeStamp, int sizeOfImage, ISymbolNotification notification)
        {
            Debug.Assert(notification != null);

            string exeIndexPath = null;
            foreach (SymPathElement element in SymbolPath.Elements)
            {
                if (element.IsSymServer)
                {
                    if (exeIndexPath == null)
                        exeIndexPath = fileName + @"\" + buildTimeStamp.ToString("x") + sizeOfImage.ToString("x") + @"\" + fileName;

                    string cache = element.Cache;
                    if (cache == null)
                        cache = SymbolPath.DefaultSymbolCache;

                    string targetPath = GetFileFromServer(element.Target, exeIndexPath, cache, notification);
                    if (targetPath != null)
                        return targetPath;
                }
                else
                {
                    string filePath = Path.Combine(element.Target, fileName);
                    _log.WriteLine("Probing file {0}", filePath);
                    if (File.Exists(filePath))
                    {
                        using (PEFile file = new PEFile(filePath))
                        {
                            // TODO: This is incorrect.
                            //if ((file.Header.TimeDateStampSec == buildTimeStamp) && (file.Header.SizeOfImage == sizeOfImage))
                            {
                                notification.FoundSymbolOnPath(filePath);
                                return filePath;
                            }

                            //m_log.WriteLine("Found file {0} but file timstamp:size {1}:{2} != desired {3}:{4}, rejecting.",
                            //    filePath, file.Header.TimeDateStampSec, file.Header.SizeOfImage, buildTimeStamp, sizeOfImage);
                        }
                    }

                    notification.ProbeFailed(filePath);
                }
            }
            return null;
        }

        // Once you have a file path to a PDB file, you can open it with this method
        /// <summary>
        /// Given the path name to a particular PDB file, load it so that you can resolve symbols in it.  
        /// </summary>
        /// <param name="symbolFilePath">The name of the PDB file to open.</param>
        /// <returns>The SymbolReaderModule that represents the information in the symbol file (PDB)</returns>
        internal SymbolModule OpenSymbolFile(string symbolFilePath)
        {
            var ret = new SymbolModule(this, symbolFilePath);
            return ret;
        }

        // Various state that controls symbol and source file lookup.  
        /// <summary>
        /// The symbol path used to look up PDB symbol files.   Set when the reader is initialized.  
        /// </summary>
        public SymPath SymbolPath { get; set; }
        /// <summary>
        /// The paths used to look up source files.  defaults to _NT_SOURCE_PATH.  
        /// </summary>
        public string SourcePath
        {
            get
            {
                if (_sourcePath == null)
                {
                    _sourcePath = Environment.GetEnvironmentVariable("_NT_SOURCE_PATH");
                    if (_sourcePath == null)
                        _sourcePath = "";
                }
                return _sourcePath;
            }
            set
            {
                _sourcePath = value;
                _parsedSourcePath = null;
            }
        }
        /// <summary>
        /// Where symbols are downloaded if needed.   Derived from symbol path
        /// </summary>
        public string SymbolCacheDirectory
        {
            get
            {
                if (_symbolCacheDirectory == null)
                    _symbolCacheDirectory = SymbolPath.DefaultSymbolCache;
                return _symbolCacheDirectory;
            }
            set
            {
                _symbolCacheDirectory = value;
            }
        }
        /// <summary>
        /// The place where source is downloaded from a source server.  
        /// </summary>
        public string SourceCacheDirectory
        {
            get
            {
                if (_sourceCacheDirectory == null)
                    _sourceCacheDirectory = Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "SrcCache");
                return _sourceCacheDirectory;
            }
            set
            {
                _sourceCacheDirectory = value;
            }
        }
        /// <summary>
        /// Is this symbol reader limited to just the local machine cache or not?
        /// </summary>
        public SymbolReaderFlags Flags { get; set; }
        /// <summary>
        /// Cache even the unsafe pdbs to the SymbolCacheDirectory.
        /// </summary>
        public bool CacheUnsafePdbs;
        /// <summary>
        /// We call back on this when we find a PDB by probing in 'unsafe' locations (like next to the EXE or in the Built location)
        /// If this function returns true, we assume that it is OK to use the PDB.  
        /// </summary>
        public Func<string, bool> SecurityCheck { get; set; }
        /// <summary>
        /// A place to log additional messages 
        /// </summary>
        public TextWriter Log { get { return _log; } }

        // TODO FIX NOW, need to call SymCleanup, but I need s_currentProcessHandle to be valid. 
#if false
        ~SymbolReader()
        {
        }
#endif
        #region Experimental
        // Experiemental TODO USE OR REMOVE 
#if false
        public static void Test()
        {
#if false
            StringWriter sw = new StringWriter();
            SymbolReader reader = new SymbolReader(sw); 
            SymbolModule module = reader.OpenSymbolFile(@"C:\Users\vancem\Documents\TraceEvent.pdb");

            foreach (var name in module.FindChildrenNames())
            {
                Trace.WriteLine("Got Name " + name);
            }
            Trace.WriteLine("Done");
#endif
            // GetPdbFromServer("http://symweb", @"clr.pdb\1E18F3E494DC464B943EA90F23E256432\clr.pdb", ".");
            // GetPdbFromServer("http://msdl.microsoft.com/download/symbols", @"clr.pdb\1E18F3E494DC464B943EA90F23E256432\clr.pdb", ".");
        }
#endif
        /// <summary>
        /// Fetches a file from the server 'serverPath' weith pdb signature path 'pdbSigPath' and places it in its
        /// correct location in 'symbolCacheDir'  Will return the path of the cached copy if it succeeds, null otherwise.  
        /// 
        /// You should probably be using GetFileFromServer
        /// </summary>
        /// <param name="serverPath">path to server (eg. \\symbols\symbols or http://symweb) </param>
        /// <param name="pdbIndexPath">pdb path with signature (e.g clr.pdb/1E18F3E494DC464B943EA90F23E256432/clr.pdb)</param>
        /// <param name="symbolCacheDir">path to the symbol cache where files are fetched (e.g. %TEMP%\symbols) </param>
        /// <param name="notification">A callback to provide progress updates.</param>
        private string GetPhysicalFileFromServer(string serverPath, string pdbIndexPath, string symbolCacheDir, ISymbolNotification notification)
        {
            if (string.IsNullOrEmpty(serverPath))
                return null;

            var fullDestPath = Path.Combine(symbolCacheDir, pdbIndexPath);
            if (!File.Exists(fullDestPath))
            {
                if (serverPath.StartsWith("http:"))
                {
                    var fullUri = serverPath + "/" + pdbIndexPath.Replace('\\', '/');
                    try
                    {
                        var req = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(fullUri);
                        req.UserAgent = "Microsoft-Symbol-Server/6.13.0009.1140";
                        var responce = req.GetResponse();
                        var fromStream = responce.GetResponseStream();
                        var dirName = Path.GetDirectoryName(fullDestPath);

                        notification.FoundSymbolOnPath(fullUri);
                        CopyStreamToFile(fromStream, fullUri, fullDestPath, notification);
                    }
                    catch (Exception e)
                    {
                        notification.ProbeFailed(fullUri);
                        _log.WriteLine("Probe of {0} failed: {1}", fullUri, e.Message);
                        return null;
                    }
                }
                else
                {
                    var fullSrcPath = Path.Combine(serverPath, pdbIndexPath);
                    if (!File.Exists(fullSrcPath))
                        return null;

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath));
                    File.Copy(fullSrcPath, fullDestPath);
                    notification.FoundSymbolInCache(fullDestPath);
                }
            }
            else
            {
                notification.FoundSymbolInCache(fullDestPath);
            }
            return fullDestPath;
        }

        /// <summary>
        /// This just copies a stream to a file path with logging.  
        /// </summary>
        private void CopyStreamToFile(Stream fromStream, string fromUri, string fullDestPath, ISymbolNotification notification)
        {
            try
            {
                int total = 0;
                var dirName = Path.GetDirectoryName(fullDestPath);
                Directory.CreateDirectory(dirName);
                _log.WriteLine("Success Copying {0} to {1}", fromUri, fullDestPath);
                using (Stream toStream = File.Create(fullDestPath))
                {
                    byte[] buffer = new byte[8192];
                    int cnt = 0;
                    for (;;)
                    {
                        int count = fromStream.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                            break;
                        toStream.Write(buffer, 0, count);

                        total += count;
                        notification.DownloadProgress(total);

                        _log.Write(".");
                        cnt++;
                        if (cnt > 40)
                        {
                            _log.WriteLine();
                            _log.Flush();
                            cnt = 0;
                        }
                        System.Threading.Thread.Sleep(0);       // allow interruption. 
                    }
                }

                notification.DownloadComplete(fullDestPath, fullDestPath[fullDestPath.Length - 1] == '_');
            }
            finally
            {
                fromStream.Close();
                _log.WriteLine();
            }
        }

#if false // TODO FIX NOW enable and replace 
        // TODO FIX NOW review and use instead of FindSymbolFilePath
        public string FindSymbolFilePath2(string pdbSimpleName, Guid pdbIndexGuid, int pdbIndexAge, string dllFilePath = null, string fileVersion = "")
        {
            string pdbPath = null;
            string pdbIndexPath = null;
            SymPath path = new SymPath(this.SymbolPath);
            foreach (SymPathElement element in path.Elements)
            {
                if (element.IsSymServer)
                {
                    if (pdbIndexPath == null)
                        pdbIndexPath = pdbSimpleName + @"\" + pdbIndexGuid.ToString("N") + pdbIndexAge.ToString() + @"\" + pdbSimpleName;
                    string cache = element.Cache;
                    if (cache == null)
                        cache = path.DefaultSymbolCache;
                    pdbPath = GetFileFromServer(element.Target, pdbIndexPath, cache);
                }
                else
                {
                    string filePath = Path.Combine(element.Target, pdbSimpleName);
                    if (File.Exists(filePath))
                    {
                        SymbolModule module = this.OpenSymbolFile(filePath);
                        if ((module.PdbGuid == pdbIndexGuid) && (module.PdbAge == pdbIndexAge))
                            pdbPath = filePath;
                    }
                }
            }
            if (pdbPath != null)
                this.m_log.WriteLine("Successfully found PDB {0}\r\n    GUID {1} Age {2} Version {3}", new object[] { pdbSimpleName, pdbIndexGuid, pdbIndexAge, fileVersion });
            return this.CacheFileLocally(pdbPath, pdbIndexGuid, pdbIndexAge);
        }
#endif 

        /// <summary>
        /// Looks up 'fileIndexPath' on the server 'urlForServer' copying the file to 'symbolCacheDir' and returning the
        /// path name there (thus it is always a local file).  Unlike  GetPhysicalFileFromServer, GetFileFromServer understands
        /// how to deal with compressed files and file.ptr (redirection).  
        /// </summary>
        /// <returns>The path to a local file or null if the file cannot be found.</returns>
        private string GetFileFromServer(string urlForServer, string fileIndexPath, string symbolCacheDir, ISymbolNotification notification)
        {
            if (String.IsNullOrEmpty(urlForServer))
                return null;

            // Just try to fetch the file directly
            var ret = GetPhysicalFileFromServer(urlForServer, fileIndexPath, symbolCacheDir, notification);
            if (ret != null)
                return ret;

            var targetPath = Path.Combine(symbolCacheDir, fileIndexPath);

            // See if it is a compressed file by replacing the last character of the name with an _
            var compressedSigPath = fileIndexPath.Substring(0, fileIndexPath.Length - 1) + "_";
            var compressedFilePath = GetPhysicalFileFromServer(urlForServer, compressedSigPath, symbolCacheDir, notification);
            if (compressedFilePath != null)
            {
                // Decompress it
                _log.WriteLine("Expanding {0} to {1}", compressedFilePath, targetPath);
                Command.Run("Expand " + Command.Quote(compressedFilePath) + " " + Command.Quote(targetPath));
                File.Delete(compressedFilePath);
                notification.DecompressionComplete(targetPath);
                return targetPath;
            }

            // See if we have a file that tells us to redirect elsewhere. 
            var filePtrSigPath = Path.Combine(Path.GetDirectoryName(fileIndexPath), "file.ptr");
            var filePtrFilePath = GetPhysicalFileFromServer(urlForServer, filePtrSigPath, symbolCacheDir, notification);
            if (filePtrFilePath != null)
            {
                var filePtrData = File.ReadAllText(filePtrFilePath).Trim();
                if (filePtrData.StartsWith("PATH:"))
                    filePtrData = filePtrData.Substring(5);

                File.Delete(filePtrFilePath);
                // TODO FIX NOW can you use file.ptr to redirect to HTTP?

                if (!filePtrData.StartsWith("MSG:") && File.Exists(filePtrData))
                {
                    _log.WriteLine("Copying {0} to {1}", filePtrData, targetPath);
                    // TODO FIX NOW don't use copyFile as it is not very interruptable, and this can take a while over a network.  
                    File.Copy(filePtrData, targetPath, true);
                    return targetPath;
                }
                else
                    _log.WriteLine("Error resolving file.Ptr: content '{0}'", filePtrData);
            }
            return null;
        }
        #endregion

        #region private
        /// <summary>
        /// Deduce the path to where CLR.dll (and in particular NGEN.exe live for the NGEN image 'ngenImagepath')
        /// Returns null if it can't be found
        /// </summary>
        private static string GetClrDirectoryForNGenImage(string ngenImagePath, TextWriter log)
        {
            string majorVersion;
            // Set the default bitness
            string bitness = "";
            var m = Regex.Match(ngenImagePath, @"^(.*)\\assembly\\NativeImages_(v(\d+)[\dA-Za-z.]*)_(\d\d)\\", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var basePath = m.Groups[1].Value;
                var version = m.Groups[2].Value;
                majorVersion = m.Groups[3].Value;
                bitness = m.Groups[4].Value;

                // See if this NGEN image was in a NIC associated with a private runtime.  
                if (basePath.EndsWith(version))
                {
                    if (Directory.Exists(basePath))
                        return basePath;
                }
            }
            else
            {
                m = Regex.Match(ngenImagePath, @"\\Microsoft\\CLR_v(\d+)\.\d+(_(\d\d))?\\NativeImages", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    majorVersion = m.Groups[1].Value;
                    bitness = m.Groups[3].Value;
                }
                else
                {
                    log.WriteLine("Warning: Could not deduce CLR version from path of NGEN image, skipping {0}", ngenImagePath);
                    return null;
                }
            }

            // Only version 4.0 of the runtime has NGEN PDB support 
            if (int.Parse(majorVersion) < 4)
            {
                log.WriteLine("Pre V4.0 native image, skipping: {0}", ngenImagePath);
                return null;
            }

            var winDir = Environment.GetEnvironmentVariable("winDir");

            // If not set, 64 bit OS means we default to 64 bit.  
            if (bitness == "" && Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null)
                bitness = "64";

            if (bitness != "64")
                bitness = "";

            var frameworkDir = Path.Combine(winDir, @"Microsoft.NET\Framework" + bitness);
            var candidates = Directory.GetDirectories(frameworkDir, "v" + majorVersion + ".*");
            if (candidates.Length != 1)
            {
                log.WriteLine("Warning: Could not find Version {0} of the .NET Framework in {1}", majorVersion, frameworkDir);
                return null;
            }
            return candidates[0];
        }

        private string _sourcePath;
        internal List<string> ParsedSourcePath
        {
            get
            {
                if (_parsedSourcePath == null)
                {
                    _parsedSourcePath = new List<string>();
                    foreach (var path in SourcePath.Split(';'))
                    {
                        var normalizedPath = path.Trim();
                        if (normalizedPath.EndsWith(@"\"))
                            normalizedPath = normalizedPath.Substring(0, normalizedPath.Length - 1);
                        if (Directory.Exists(normalizedPath))
                            _parsedSourcePath.Add(normalizedPath);
                        else
                            _log.WriteLine("Path {0} in source path does not exist, skipping.", normalizedPath);
                    }
                }
                return _parsedSourcePath;
            }
        }
        internal List<string> _parsedSourcePath;

        private bool CheckSecurity(string pdbName)
        {
            if (SecurityCheck == null)
            {
                _log.WriteLine("Found PDB {0}, however this is in an unsafe location.", pdbName);
                _log.WriteLine("If you trust this location, place this directory the symbol path to correct this.");
                return false;
            }
            if (!SecurityCheck(pdbName))
            {
                _log.WriteLine("Found PDB {0}, but failed securty check.", pdbName);
                return false;
            }
            return true;
        }

        /// <summary>
        /// This is an optional routine.  It is already the case that if you find a PDB on a symbol server
        /// that it will be cached locally, however if you find it on a network path by NOT using a symbol
        /// server, it will be used in place.  This is annoying, and this routine makes up for this by
        /// mimicking this behavior.  Basically if pdbPath is not a local file name, it will copy it to
        /// the local symbol cache and return the local path. 
        /// </summary>
        private string CacheFileLocally(string pdbPath, Guid pdbGuid, int pdbAge)
        {
            try
            {
                var fileName = Path.GetFileName(pdbPath);

                // Use SymSrv conventions in the cache if the Guid is non-zero, otherwise we simply place it in the cache.  
                var localPdbDir = SymbolCacheDirectory;
                if (pdbGuid != Guid.Empty)
                {
                    var pdbPathPrefix = Path.Combine(SymbolCacheDirectory, fileName);
                    // There is a non-trivial possibility that someone puts a FILE that is named what we want the dir to be.  
                    if (File.Exists(pdbPathPrefix))
                    {
                        // If the pdb path happens to be the SymbolCacheDir (a definate possibility) then we would
                        // clobber the source file in our attempt to set up the target.  In this case just give up
                        // and leave the file as it was.  
                        if (string.Compare(pdbPath, pdbPathPrefix, StringComparison.OrdinalIgnoreCase) == 0)
                            return pdbPath;
                        _log.WriteLine("Removing file {0} from symbol cache to make way for symsrv files.", pdbPathPrefix);
                        File.Delete(pdbPathPrefix);
                    }

                    localPdbDir = Path.Combine(pdbPathPrefix, pdbGuid.ToString("N") + pdbAge.ToString());
                }
                else if (!CacheUnsafePdbs)
                    return pdbPath;

                if (!Directory.Exists(localPdbDir))
                    Directory.CreateDirectory(localPdbDir);

                var localPdbPath = Path.Combine(localPdbDir, fileName);
                var fileExists = File.Exists(localPdbPath);
                if (!fileExists || File.GetLastWriteTimeUtc(localPdbPath) != File.GetLastWriteTimeUtc(pdbPath))
                {
                    if (fileExists)
                        _log.WriteLine("WARNING: overwriting existing file {0}.", localPdbPath);

                    _log.WriteLine("Copying {0} to local cache {1}", pdbPath, localPdbPath);
                    File.Copy(pdbPath, localPdbPath, true);
                }
                return localPdbPath;
            }
            catch (Exception e)
            {
                _log.WriteLine("Error trying to update local PDB cache {0}", e.Message);
            }
            return pdbPath;
        }

        private static bool StatusCallback(
            IntPtr hProcess,
            SymbolReaderNativeMethods.SymCallbackActions ActionCode,
            ulong UserData,
            ulong UserContext)
        {
            bool ret = false;
            switch (ActionCode)
            {
                case SymbolReaderNativeMethods.SymCallbackActions.CBA_SRCSRV_INFO:
                case SymbolReaderNativeMethods.SymCallbackActions.CBA_DEBUG_INFO:
                    var line = new String((char*)UserData).Trim();
                    ret = true;
                    break;
                default:
                    // messages.Append("STATUS: Code=").Append(ActionCode).AppendLine();
                    break;
            }
            return ret;
        }

        /// <summary>
        /// We may be a 32 bit app which has File system redirection turned on
        /// Morph System32 to SysNative in that case to bypass file system redirection         
        /// </summary>
        internal string BypassSystem32FileRedirection(string path)
        {
            var winDir = Environment.GetEnvironmentVariable("WinDir");
            if (winDir != null)
            {
                var system32 = Path.Combine(winDir, "System32");
                if (path.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
                {
                    if (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null)
                    {
                        var sysNative = Path.Combine(winDir, "Sysnative");
                        var newPath = Path.Combine(sysNative, path.Substring(system32.Length + 1));
                        if (File.Exists(newPath))
                            path = newPath;
                    }
                }
            }
            return path;
        }

        private static bool? s_winBuildsExist;
        private static bool WinBuildsExist
        {
            get
            {
                if (!s_winBuildsExist.HasValue)
                    s_winBuildsExist = SymPath.ComputerNameExists("winbuilds");
                return s_winBuildsExist.Value;
            }
        }
        private static bool? s_cpvsbuildExist;
        private static bool CpvsbuildExist
        {
            get
            {
                if (!s_cpvsbuildExist.HasValue)
                    s_cpvsbuildExist = SymPath.ComputerNameExists("cpvsbuild");
                return s_cpvsbuildExist.Value;
            }
        }

        private static string s_symPath;
        private static Process s_currentProcess;      // keep to insure currentProcessHandle stays alive
        internal static IntPtr s_currentProcessHandle; // TODO really need to get on safe handle plan 
        private static SymbolReaderNativeMethods.SymRegisterCallbackProc s_callback;
        internal TextWriter _log;

        private string _symbolCacheDirectory;
        private string _sourceCacheDirectory;
        #endregion
    }

    /// <summary>
    /// A symbolReaderModule represents a single PDB.   You get one from SymbolReader.OpenSymbolFile
    /// It is effecively a managed interface to the Debug Interface Access (DIA) see 
    /// http://msdn.microsoft.com/en-us/library/x93ctkx8.aspx for more.   I have only exposed what
    /// I need, and the interface is quite large (and not super pretty).  
    /// </summary>
    internal unsafe class SymbolModule
    {
        /// <summary>
        /// The path name to the PDB itself
        /// </summary>
        public string PdbPath { get { return _pdbPath; } }
        /// <summary>
        /// This is the EXE associated with the Pdb.  It may be null or an invalid path.  It is used
        /// to help look up source code (it is implicitly part of the Source Path search) 
        /// </summary>
        public string ExePath { get; set; }

        public IDiaSession Session
        {
            get
            {
                return _session;
            }
        }


        /// <summary>
        /// Finds a (method) symbolic name for a given relative virtual address of some code.  
        /// Returns an empty string if a name could not be found. 
        /// </summary>
        public string FindNameForRva(uint rva)
        {
            System.Threading.Thread.Sleep(0);           // Allow cancelation.  
            if (_symbolsByAddr == null)
                return "";
            IDiaSymbol symbol = _symbolsByAddr.symbolByRVA(rva);
            if (symbol == null)
            {
                Debug.WriteLine(string.Format("Warning: address 0x{0:x} not found.", rva));
                return "";
            }
            var ret = symbol.name;
            if (ret == null)
            {
                Debug.WriteLine(string.Format("Warning: address 0x{0:x} had a null symbol name.", rva));
                return "";
            }
            if (ret.Length == 0)
            {
                Debug.WriteLine(string.Format("Warning: address 0x{0:x} symbol {1} has length 0", rva, ret));
                return "";
            }
            // TODO FIX NOW, should not need to do this hand-unmangling.
            if (ret.Contains("@"))
            {
                // TODO relativel inefficient.  
                string unmangled = null;
                symbol.get_undecoratedNameEx(0x1000, out unmangled);
                if (unmangled != null)
                    ret = unmangled;

                if (ret.StartsWith("@"))
                    ret = ret.Substring(1);
                if (ret.StartsWith("_"))
                    ret = ret.Substring(1);

#if false // TODO FIX NOW remove  
                var m = Regex.Match(ret, @"(.*)@\d+$");
                if (m.Success)
                    ret = m.Groups[1].Value;
                else
                    Debug.WriteLine(string.Format("Warning: address 0x{0:x} symbol {1} has a mangled name.", rva, ret));
#else
                var atIdx = ret.IndexOf('@');
                if (0 < atIdx)
                    ret = ret.Substring(0, atIdx);
#endif
            }

            return ret;
        }

        /// <summary>
        /// Fetches the source location (line number and file), given the relative virtual address (RVA)
        /// of the location in the executable.  
        /// </summary>
        public SourceLocation SourceLocationForRva(uint rva)
        {
            _reader._log.WriteLine("SourceLocationForRva: looking up RVA {0:x} ", rva);

            uint fetchCount;
            IDiaEnumLineNumbers sourceLocs;
            _session.findLinesByRVA(rva, 0, out sourceLocs);
            IDiaLineNumber sourceLoc;
            sourceLocs.Next(1, out sourceLoc, out fetchCount);
            if (fetchCount == 0)
            {
                _reader._log.WriteLine("SourceLocationForRva: No lines for RVA {0:x} ", rva);
                return null;
            }
            var buildTimeSourcePath = sourceLoc.sourceFile.fileName;
            var lineNum = (int)sourceLoc.lineNumber;

            var sourceLocation = new SourceLocation(sourceLoc);
            return sourceLocation;
        }
        /// <summary>
        /// Managed code is shipped as IL, so RVA to NATIVE mapping can't be placed in the PDB. Instead
        /// what is placed in the PDB is a mapping from a method's meta-data token and IL offset to source
        /// line number.  Thus if you have a metadata token and IL offset, you can again get a source location
        /// </summary>
        public SourceLocation SourceLocationForManagedCode(uint methodMetaDataToken, int ilOffset)
        {
            _reader._log.WriteLine("SourceLocationForManagedCode: Looking up method token {0:x} ilOffset {1:x}", methodMetaDataToken, ilOffset);

            IDiaSymbol methodSym;
            _session.findSymbolByToken(methodMetaDataToken, SymTagEnum.SymTagFunction, out methodSym);
            if (methodSym == null)
            {
                _reader._log.WriteLine("SourceLocationForManagedCode: No symbol for token {0:x} ilOffset {1:x}", methodMetaDataToken, ilOffset);
                return null;
            }

            uint fetchCount;
            IDiaEnumLineNumbers sourceLocs;
            IDiaLineNumber sourceLoc;

            // TODO FIX NOW, this code here is for debugging only turn if off when we are happy.  
            //m_session.findLinesByRVA(methodSym.relativeVirtualAddress, (uint)(ilOffset + 256), out sourceLocs);
            //for (int i = 0; ; i++)
            //{
            //    sourceLocs.Next(1, out sourceLoc, out fetchCount);
            //    if (fetchCount == 0)
            //        break;
            //    if (i == 0)
            //        m_reader.m_log.WriteLine("SourceLocationForManagedCode: source file: {0}", sourceLoc.sourceFile.fileName);
            //    m_reader.m_log.WriteLine("SourceLocationForManagedCode: ILOffset {0:x} -> line {1}",
            //        sourceLoc.relativeVirtualAddress - methodSym.relativeVirtualAddress, sourceLoc.lineNumber);
            //} // End TODO FIX NOW debugging code

            // For managed code, the 'RVA' is a 'cumulative IL offset' (amount of IL bytes before this in the module)
            // Thus you find the line number of a particular IL offset by adding the offset within the method to
            // the cumulative IL offset of the start of the method.  
            _session.findLinesByRVA(methodSym.relativeVirtualAddress + (uint)ilOffset, 256, out sourceLocs);
            sourceLocs.Next(1, out sourceLoc, out fetchCount);
            if (fetchCount == 0)
            {
                _reader._log.WriteLine("SourceLocationForManagedCode: No lines for token {0:x} ilOffset {1:x}", methodMetaDataToken, ilOffset);
                return null;
            }

            int lineNum;
            // FEEFEE is some sort of illegal line number that is returned some time,  It is better to ignore it.  
            // and take the next valid line
            for (;;)
            {
                lineNum = (int)sourceLoc.lineNumber;
                if (lineNum != 0xFEEFEE)
                    break;
                sourceLocs.Next(1, out sourceLoc, out fetchCount);
                if (fetchCount == 0)
                    break;
            }

            var sourceLocation = new SourceLocation(sourceLoc);
            return sourceLocation;
        }
        /// <summary>
        /// Returns a list of all source files referenced in the PDB
        /// </summary>
        public IEnumerable<SourceFile> AllSourceFiles()
        {
            IDiaEnumTables tables;
            _session.getEnumTables(out tables);

            IDiaEnumSourceFiles sourceFiles;
            IDiaTable table = null;
            uint fetchCount = 0;
            for (;;)
            {
                tables.Next(1, ref table, ref fetchCount);
                if (fetchCount == 0)
                    return null;
                sourceFiles = table as IDiaEnumSourceFiles;
                if (sourceFiles != null)
                    break;
            }

            var ret = new List<SourceFile>();
            IDiaSourceFile sourceFile = null;
            for (;;)
            {
                sourceFiles.Next(1, out sourceFile, out fetchCount);
                if (fetchCount == 0)
                    break;
                ret.Add(new SourceFile(this, sourceFile));
            }
            return ret;
        }

        /// <summary>
        /// The a unique identifier that is used to relate the DLL and its PDB.   
        /// </summary>
        public Guid PdbGuid { get { return _session.globalScope.guid; } }
        /// <summary>
        /// Along with the PdbGuid, there is a small integer 
        /// call the age is also used to find the PDB (it represents the differnet 
        /// post link transformations the DLL has undergone).  
        /// </summary>
        public int PdbAge { get { return (int)_session.globalScope.age; } }

        /// <summary>
        /// The symbol reader this SymbolModule was created from.  
        /// </summary>
        public SymbolReader SymbolReader { get { return _reader; } }

        #region private
#if false
        // TODO FIX NOW use or remove
        internal enum NameSearchOptions
        {
            nsNone,
            nsfCaseSensitive = 0x1,
            nsfCaseInsensitive = 0x2,
            nsfFNameExt = 0x4,                  // treat as a file path
            nsfRegularExpression = 0x8,         // * and ? wildcards
            nsfUndecoratedName = 0x10,          // A undecorated name is the name you see in the source code.  
        };
        // TODO FIX NOW REMOVE
        public IEnumerable<string> FindChildrenNames()
        {
            return FindChildrenNames(m_session.globalScope);
        }
        // TODO FIX NOW REMOVE
        internal IEnumerable<string> FindChildrenNames(IDiaSymbol scope,
            string name = null, NameSearchOptions searchOptions = NameSearchOptions.nsNone)
        {
            var syms = FindChildren(m_session.globalScope, name, searchOptions);
            var ret = new List<string>();
            foreach (var sym in syms)
                ret.Add(sym.name);
            return ret;
        }
        // TODO FIX NOW REMOVE
        private IEnumerable<IDiaSymbol> FindChildren(IDiaSymbol scope,
            string name = null, NameSearchOptions searchOptions = NameSearchOptions.nsNone)
        {
            IDiaEnumSymbols symEnum;
            m_session.findChildren(scope, SymTagEnum.SymTagNull, name, (uint)searchOptions, out symEnum);
            uint fetchCount;

            var ret = new List<IDiaSymbol>();
            for (; ; )
            {
                IDiaSymbol sym;

                symEnum.Next(1, out sym, out fetchCount);
                if (fetchCount == 0)
                    break;

                SymTagEnum symTag = (SymTagEnum)sym.symTag;
                Debug.WriteLine("Got " + sym.name + " symTag  " + symTag + " token " + sym.token.ToString("x"));
                if (symTag == SymTagEnum.SymTagFunction)
                {
                    if (sym.token != 0)
                    {
                        var sourceLocation = SourceLocationForManagedCode(sym.token, 0);
                        if (sourceLocation != null)
                            Debug.WriteLine("Got Line " + sourceLocation.LineNumber + " file " + sourceLocation.SourceFile);
                    }
                }

                if (symTag == SymTagEnum.SymTagCompiland)
                {
                    var children = (List<IDiaSymbol>)FindChildren(sym, name, searchOptions);
                    Debug.WriteLine("got " + children.Count + " children");
                }

                ret.Add(sym);
            }
            return ret;
        }
#endif

        public SymbolModule(SymbolReader reader, string pdbFilePath)
        {
            _pdbPath = pdbFilePath;
            this._reader = reader;

            IDiaDataSource source = DiaLoader.GetDiaSourceObject();
            source.loadDataFromPdb(pdbFilePath);
            source.openSession(out _session);
            _session.getSymbolsByAddr(out _symbolsByAddr);
        }

        internal void LogManagedInfo(string pdbName, Guid pdbGuid, int pdbAge)
        {
            // Simply rember this if we decide we need it for source server support
            _managedPdbName = pdbName;
            _managedPdbGuid = pdbGuid;
            _managedPdbAge = pdbAge;
        }

        // returns the path of the PDB that has source server information in it (which for NGEN images is the PDB for the managed image)
        internal string SourceServerPdbPath
        {
            get
            {
                if (_managedPdbName == null)
                    return PdbPath;
                if (!_managedPdbPathAttempted)
                {
                    _managedPdbPathAttempted = true;
                    _managedPdbPath = _reader.FindSymbolFilePath(_managedPdbName, _managedPdbGuid, _managedPdbAge);
                }
                if (_managedPdbPath == null)
                    return PdbPath;
                return _managedPdbPath;
            }
        }
        private string _managedPdbName;
        private Guid _managedPdbGuid;
        private int _managedPdbAge;
        private string _managedPdbPath;
        private bool _managedPdbPathAttempted;

        internal SymbolReader _reader;
        private IDiaSession _session;
        private IDiaEnumSymbolsByAddr _symbolsByAddr;
        private string _pdbPath;

        #endregion

        // TODO use or remove. 
        internal void GetSourceServerStream()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName);

            var pdbStrExe = Path.Combine(dir, "pdbstr.exe");
            var cmd = Command.Run(Command.Quote(pdbStrExe) + "-s:srcsrv -p:" + _pdbPath);
            string output = cmd.Output;
        }
    }

    /// <summary>
    /// SymbolReaderFlags indicates preferences on how agressively symbols should be looked up.  
    /// </summary>
    [Flags]
    public enum SymbolReaderFlags
    {
        /// <summary>
        /// No options this is the common case, where you want to look up everything you can. 
        /// </summary>
        None = 0,
        /// <summary>
        /// Only fetch the PDB if it lives in the symbolCacheDirectory (is local an is generated).  
        /// This will generate NGEN pdbs unless the NoNGenPDBs flag is set. 
        /// </summary>
        CacheOnly = 1,
        /// <summary>
        /// No NGEN PDB generation.  
        /// </summary>
        NoNGenPDB = 2,
    }

    /// <summary>
    /// A source file represents a source file from a PDB.  This is not just a string
    /// because the file has a build time path, a checksum, and it needs to be 'smart'
    /// to copy down the file if requested.  
    /// </summary>
    internal class SourceFile
    {
        /// <summary>
        /// The path of the file at the time the source file was built. 
        /// </summary>
        public string BuildTimeFilePath { get; private set; }
        /// <summary>
        /// true if the PDB has a checksum for the data in the source file. 
        /// </summary>
        public bool HasChecksum { get { return _hash != null; } }
        /// <summary>
        /// This may fetch things from the source server, and thus can be very slow, which is why it is not a property. 
        /// </summary>
        /// <returns></returns>
        public string GetSourceFile(bool requireChecksumMatch = false)
        {
            _checkSumMatches = false;
            _getSourceCalled = true;
            var log = _symbolModule._reader._log;
            string bestGuess = null;

            // Did we build on this machine?  
            if (File.Exists(BuildTimeFilePath))
            {
                bestGuess = BuildTimeFilePath;
                _checkSumMatches = ChecksumMatches(BuildTimeFilePath);
                if (_checkSumMatches)
                {
                    log.WriteLine("Found in build location.");
                    return BuildTimeFilePath;
                }
            }

            // Try the source server 
            var ret = GetSourceFromSrcServer();
            if (ret != null)
            {
                log.WriteLine("Got source from source server.");
                _checkSumMatches = true;       // TODO we assume source server is right, is that OK? 
                return ret;
            }
            log.WriteLine("Not present at {0} or on source server, looking on NT_SOURCE_PATH");

            // Try _NT_SOURCE_PATH
            var locations = _symbolModule._reader.ParsedSourcePath;
            log.WriteLine("_NT_SOURCE_PATH={0}", _symbolModule._reader.SourcePath);

            // If we know the exe path, add that to the search path.   
            if (_symbolModule.ExePath != null)
            {
                var exeDir = Path.GetDirectoryName(_symbolModule.ExePath);
                if (Directory.Exists(exeDir))
                {
                    // Add directories up the path, we stop somewhat arbitrarily at 3 
                    for (int i = 0; i < 3; i++)
                    {
                        locations.Insert(0, exeDir);
                        log.WriteLine("Adding Exe path {0}", exeDir);

                        exeDir = Path.GetDirectoryName(exeDir);
                        if (exeDir == null)
                            break;
                    }
                }
            }

            var curIdx = 0;
            for (;;)
            {
                var sepIdx = BuildTimeFilePath.IndexOf('\\', curIdx);
                if (sepIdx < 0)
                    break;
                curIdx = sepIdx + 1;
                var tail = BuildTimeFilePath.Substring(sepIdx);

                foreach (string location in locations)
                {
                    var probe = location + tail;
                    log.WriteLine("Probing {0}", probe);
                    if (File.Exists(probe))
                    {
                        if (bestGuess != null)
                            bestGuess = probe;
                        _checkSumMatches = ChecksumMatches(probe);
                        if (_checkSumMatches)
                        {
                            log.WriteLine("Success {0}", probe);
                            return probe;
                        }
                        else
                            log.WriteLine("Found file {0} but checksum mismatches", probe);
                    }
                }
            }

            if (!requireChecksumMatch && bestGuess != null)
            {
                log.WriteLine("[Warning: Checksum mismatch for {0}]", bestGuess);
                return bestGuess;
            }

            log.WriteLine("[Could not find source for {0}]", BuildTimeFilePath);
            return null;
        }

        /// <summary>
        /// If GetSourceFile is called and 'requireChecksumMatch' == false then you can call this property to 
        /// determine if the checksum actually matched or not.   This will return true if the original
        /// PDB does not have a checksum (HasChecksum == false)
        /// </summary>
        public bool CheckSumMatches
        {
            get
            {
                Debug.Assert(_getSourceCalled);
                return _checkSumMatches;
            }
        }

        #region private
        private string GetSourceFromSrcServer()
        {
            string ret = null;
            lock (this)
            {
                // To allow for cancelation we run this on another thread
                // This is a workaround until I can stop using the ugly non-thread safe APIs.  
                if (s_sourceServerCommandInProgress)
                {
                    while (s_sourceServerCommandInProgress)
                        System.Threading.Thread.Sleep(100);
                }
                s_sourceServerCommandInProgress = true;
                var thread = new System.Threading.Thread(delegate ()
                {
                    ret = GetSourceFromSrcServer(BuildTimeFilePath);
                    s_sourceServerCommandInProgress = false;
                });
                thread.Start();

                // wait, allowing cancelation.  
                while (s_sourceServerCommandInProgress)
                    System.Threading.Thread.Sleep(100);
            }
            return ret;
        }

        private bool ChecksumMatches(string filePath)
        {
            if (!HasChecksum)
                return true;

            byte[] checksum = ComputeHash(filePath);
            if (checksum.Length != _hash.Length)
                return false;
            for (int i = 0; i < checksum.Length; i++)
                if (checksum[i] != _hash[i])
                    return false;
            return true;
        }

        private static bool s_sourceServerCommandInProgress;

        unsafe private string GetSourceFromSrcServer(string buildTimeSourcePath)
        {
            // Currently we are very inefficient loading and unloading constantly.  
            ulong imageBase = 0x10000;
            var sb = new StringBuilder(260);

            var reader = _symbolModule._reader;

            reader._log.WriteLine("[Searching source server for {0}]", buildTimeSourcePath);
            ulong imageBaseRet = SymbolReaderNativeMethods.SymLoadModuleExW(
                SymbolReader.s_currentProcessHandle, IntPtr.Zero, _symbolModule.SourceServerPdbPath, null, (ulong)imageBase, 0, null, 0);

            // TODO FIX NOW
            // Allow it to find exes next to the current assembly

            var origPath = Environment.GetEnvironmentVariable("PATH");
            var newPath = origPath;

            // TODO FIX NOW search harder.
            var progFiles = Environment.GetEnvironmentVariable("ProgramFiles (x86)");
            if (progFiles == null)
                progFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (progFiles != null)
            {
                var VSDir = Path.Combine(progFiles, @"Microsoft Visual Studio 11.0\Common7\IDE");
                if (!Directory.Exists(VSDir))
                    VSDir = Path.Combine(progFiles, @"Microsoft Visual Studio 10.0\Common7\IDE");

                var tfexe = Path.Combine(VSDir, "tf.exe");
                if (File.Exists(tfexe))
                {
                    reader._log.WriteLine("Adding {0} to path", newPath);
                    newPath = VSDir + ";" + newPath;
                }
                else
                {
#if !PUBLIC_ONLY
                    string standAloneTF = @"\\clrmain\tools\StandaAloneTF";
                    if (SymPath.ComputerNameExists("clrmain") && Directory.Exists(standAloneTF))
                    {
                        reader._log.WriteLine("Adding {0} to path", standAloneTF);
                        newPath = VSDir + ";" + standAloneTF;
                    }
                    else
                    {
                        reader._log.WriteLine("Warning, could not find VS installation for TF.exe, fetching Devdiv sources may fail.");
                        reader._log.WriteLine("Put TF.exe on your path to fix.");
                    }
#endif
                }
            }

            var curAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            var curAssemblyDir = Path.GetDirectoryName(curAssembly.ManifestModule.FullyQualifiedName);
            reader._log.WriteLine("Adding {0} to the path", curAssemblyDir);
            newPath = curAssemblyDir + ";" + newPath;
            Environment.SetEnvironmentVariable("PATH", newPath);

            var setHomeRet = SymbolReaderNativeMethods.SymSetHomeDirectoryW(SymbolReader.s_currentProcessHandle, reader.SourceCacheDirectory);
            Debug.Assert(!(setHomeRet == IntPtr.Zero));

            var ret = SymbolReaderNativeMethods.SymGetSourceFileW(SymbolReader.s_currentProcessHandle, imageBase, IntPtr.Zero, buildTimeSourcePath, sb, sb.Capacity);
            reader._log.WriteLine("Called SymGetSourceFileW ret = {0}", ret);
            SymbolReaderNativeMethods.SymUnloadModule64(SymbolReader.s_currentProcessHandle, imageBase);

            // TODO worry about exceptions.  
            Environment.SetEnvironmentVariable("PATH", origPath);

            if (!ret)
            {
                reader._log.WriteLine("Source Server for {0} failed", buildTimeSourcePath);
                return null;
            }

            var retVal = sb.ToString();
            reader._log.WriteLine("Source Server downloaded {0}", retVal);
            return retVal;
        }

        unsafe internal SourceFile(SymbolModule module, IDiaSourceFile sourceFile)
        {
            _symbolModule = module;
            BuildTimeFilePath = sourceFile.fileName;

            // 0 No checksum present.
            // 1 CALG_MD5 checksum generated with the MD5 hashing algorithm.
            // 2 CALG_SHA1 checksum generated with the SHA1 hashing algorithm.
            _hashType = sourceFile.checksumType;
            if (_hashType != 1 && _hashType != 0)
            {
                // TODO does anyone use SHA1?   
                Debug.Assert(false, "Unknown hash type");
                _hashType = 0;
            }
            if (_hashType != 0)
            {
                // MD5 is 16 bytes
                // SHA1 is 20 bytes  
                _hash = new byte[16];

                uint bytesFetched;
                fixed (byte* bufferPtr = _hash)
                    sourceFile.get_checksum((uint)_hash.Length, out bytesFetched, out *bufferPtr);
                Debug.Assert(bytesFetched == 16);
            }
        }

        private byte[] ComputeHash(string filePath)
        {
            System.Security.Cryptography.MD5CryptoServiceProvider crypto = new System.Security.Cryptography.MD5CryptoServiceProvider();
            using (var fileStream = File.OpenRead(filePath))
                return crypto.ComputeHash(fileStream);
        }

        private SymbolModule _symbolModule;
        private uint _hashType;
        private byte[] _hash;
        private bool _getSourceCalled;
        private bool _checkSumMatches;
        #endregion
    }
}


/// <summary>
/// The DiaLoader class knows how to load the msdia120.dll (the Debug Access Interface) (see docs at
/// http://msdn.microsoft.com/en-us/library/x93ctkx8.aspx), without it being registered as a COM object.
/// Basically it just called the DllGetClassObject interface directly.
/// 
/// It has one public method 'GetDiaSourceObject' which knows how to create a IDiaDataSource object. 
/// From there you can do anything you need.  
/// 
/// In order to get IDiaDataSource3 which includes'getStreamSize' API, you need to use the 
/// vctools\langapi\idl\dia2_internal.idl file from devdiv to produce Interop.Dia2Lib.dll
/// 
/// roughly what you need to do is 
///     copy vctools\langapi\idl\dia2_internal.idl .
///     copy vctools\langapi\idl\dia2.idl .
///     copy vctools\langapi\include\cvconst.h .
///     Change dia2.idl to include interface IDiaDataSource3 inside library Dia2Lib->importlib->coclass DiaSource
///     midl dia2_internal.idl /D CC_DP_CXX
///     tlbimp dia2_internal.tlb
///     xcopy Dia2Lib.dll Interop.Dia2Lib.dll
/// </summary>
internal static class DiaLoader
{
    /// <summary>
    /// Load the msdia100 dll and get a IDiaDataSource from it.  This is your gateway to PDB reading.   
    /// </summary>
    public static IDiaDataSource GetDiaSourceObject()
    {
        if (!Microsoft.Diagnostics.Runtime.Desktop.NativeMethods.LoadNative("msdia120.dll"))
            throw new ClrDiagnosticsException("Could not load native DLL msdia120.dll HRESULT=0x" + Marshal.GetHRForLastWin32Error().ToString("x"), ClrDiagnosticsException.HR.ApplicationError);

        var diaSourceClassGuid = new Guid("{3BFCEA48-620F-4B6B-81F7-B9AF75454C7D}");
        var comClassFactory = (IClassFactory)DllGetClassObject(diaSourceClassGuid, typeof(IClassFactory).GUID);

        object comObject = null;
        Guid iDataDataSourceGuid = typeof(IDiaDataSource).GUID;
        comClassFactory.CreateInstance(null, ref iDataDataSourceGuid, out comObject);
        return (comObject as IDiaDataSource);
    }

    #region private
    [ComImport, ComVisible(false), Guid("00000001-0000-0000-C000-000000000046"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        void CreateInstance([MarshalAs(UnmanagedType.Interface)] object aggregator,
                            ref Guid refiid,
                            [MarshalAs(UnmanagedType.Interface)] out object createdObject);
        void LockServer(bool incrementRefCount);
    }

    // Methods
    [return: MarshalAs(UnmanagedType.Interface)]
    [DllImport("msdia120.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern object DllGetClassObject(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid);

    #endregion
}