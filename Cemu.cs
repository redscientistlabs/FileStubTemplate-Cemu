namespace FileStub.Templates
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Data;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Runtime.Remoting.Messaging;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using System.Xml;
    using System.Xml.Linq;
    using System.Xml.Serialization;
    using Cemu;
    using Newtonsoft.Json;
    using RTCV.Common;
    using RTCV.CorruptCore;
    using RTCV.UI;

    public partial class FileStubTemplateCemu : Form, IFileStubTemplate
    {

        const string CEMUSTUB_RPX = "Cemu : Wii U RPX Executables";

        public string expectedCemuVersion { get; set; } = "2.0";
        public string expectedCemuTitle => "Cemu " + expectedCemuVersion;

        public string cemuDir = Path.Combine(FileStub.FileWatch.currentDir, "CEMU");
        public string cemuPath { get; set; }

        Process cemuProcess = null;

        private CemuState _state = CemuState.UNFOUND;

        private CemuState state
        {
            get => _state;
            set
            {
                Console.WriteLine($"Setting state to {value}");
                _state = value;
            }
        }

        public Dictionary<string, CemuStubSession> knownGamesDico = new Dictionary<string, CemuStubSession>();
        public CemuStubSession currentSession = new CemuStubSession();

        public bool DontSelectGame = false;

        string currentSelectedTemplate = null;
        public string[] TemplateNames
        {
            get => new string[] {
            CEMUSTUB_RPX
        };
        }

        public bool DisplayDragAndDrop => false;
        public bool DisplayBrowseTarget => false;


        public FileStubTemplateCemu()
        {
            InitializeComponent();

            tbExpectedVersion.Text = expectedCemuVersion;
            tbExpectedVersion.TextChanged += TbExpectedVersion_TextChanged;


            //ensure cemu folders exist
            if (!Directory.Exists(cemuDir))
                Directory.CreateDirectory(cemuDir);

            string cemuParamsDir = Path.Combine(cemuDir, "PARAMS");

            if (!Directory.Exists(cemuParamsDir))
                Directory.CreateDirectory(cemuParamsDir);
            if (File.Exists(Path.Combine(cemuParamsDir, "PATHNAME")))
            {
                cemuPath = File.ReadAllText(Path.Combine(cemuParamsDir, "PATHNAME"));
            }
        }

        private void TbExpectedVersion_TextChanged(object sender, EventArgs e)
        {
            expectedCemuVersion = tbExpectedVersion.Text;
        }
        public FileTarget[] GetTargets()
        {
            DontSelectGame = true;

            string targetRpx;

            if (cbSelectedGame.SelectedIndex <= 0)
            {

                targetRpx = SearchForCemuInstance();

                if (string.IsNullOrEmpty(targetRpx))
                {
                    MessageBox.Show("No target loaded");
                    return null;
                }
            }
            else
            {
                targetRpx = currentSession.gameRpxPath;
            }

            List<FileTarget> targets = new List<FileTarget>();

            var exeFileInfo = new FileInfo(targetRpx);
            var exeFolder = exeFileInfo.Directory.FullName;
            var baseFolder = exeFileInfo.Directory;

            //Build targets and backup the work rpx
            string baseless(string path) => path.Replace(exeFolder, "");
            var rpxTarget = Vault.RequestFileTarget(baseless(exeFileInfo.FullName), baseFolder.FullName);
            targets.Add(rpxTarget);
            //Prepare filestub for execution
            var sf = S.GET<StubForm>();
            FileWatch.currentSession.selectedExecution = ExecutionType.EXECUTE_OTHER_PROGRAM;
            Executor.otherProgram = currentSession.cemuExeFile.FullName;
            sf.tbArgs.Text = $"-g \"{currentSession.gameRpxPath}\"";
            FileWatch.currentSession.bigEndian = true;
            return targets.ToArray();
        }

        public void GetSegments(FileInterface exeInterface)
        {
            ELFHelper rpx = new ELFHelper(exeInterface);
            string exePath = exeInterface.Filename;
            var rpxInfo = new FileInfo(exePath);
            int i = 0;
            //List<FileInterface> segmentInterfaces = new List<FileInterface>();
            //List<MemoryDomainProxy> memoryDomainProxies = new List<MemoryDomainProxy>();
            while (i < rpx.sht_entries)
            {
                i++;
                long[] range = new long[2];
                range[0] = rpx.ss_offsets[i];
                range[1] = rpx.ss_offsets[i] + rpx.ss_sizes[i];
                string vmdnametext = rpxInfo.Name + "|Section" + i;
                if (range[0] >= range[1])
                {
                    return;
                }

                List<long[]> ranges = new List<long[]>();
                ranges.Add(range);
                VmdPrototype vmdPrototype = new VmdPrototype();
                vmdPrototype.GenDomain = exeInterface.ToString();
                vmdPrototype.BigEndian = exeInterface.BigEndian;
                vmdPrototype.AddRanges = ranges;
                vmdPrototype.WordSize = exeInterface.WordSize;
                vmdPrototype.VmdName = vmdnametext;
                vmdPrototype.PointerSpacer = 1;
                if (range[1] <= exeInterface.Size)
                {
                    RTCV.NetCore.LocalNetCoreRouter.Route(RTCV.NetCore.Endpoints.UI, RTCV.NetCore.Commands.Remote.DomainVMDAdd, (object)vmdPrototype, true);
                    RTCV.NetCore.LocalNetCoreRouter.Route(RTCV.NetCore.Endpoints.UI, RTCV.NetCore.Commands.Remote.EventDomainsUpdated);
                }
            }
        }
        public Form GetTemplateForm(string name)
        {
            this.SummonTemplate(name);
            return this;
        }

        private void SummonTemplate(string name)
        {
            currentSelectedTemplate = name;

            lbTemplateDescription.Text =
$@"== Corrupt Wii U RPX files ==
Load a game in Cemu and after it has loaded, click on Load targets into RTCV.
";
        }

        bool IFileStubTemplate.DragDrop(string[] fd)
        {
            //NOT SUPPORTED
            return true;
        }

        private Process getCemuProcess()
        {
            if (cemuProcess == null)
            {
                RefreshCemuProcess();
            }
            //Get a new process object from then pid we have.
            try
            {
                if (cemuProcess?.Id != null)
                    cemuProcess = Process.GetProcessById(cemuProcess.Id);
            }
            catch (Exception e)
            {
                cemuProcess = null;
                Console.WriteLine($"Couldn't get process from pid {cemuProcess?.Id ?? -1}\n {e}");
            }
            //If the title is still expectedCemuTitle, we know something else didn't eat the pid
            if (!(cemuProcess?.MainWindowTitle.Contains(expectedCemuTitle) ?? false))
                RefreshCemuProcess();

            return cemuProcess;
        }

        public void RefreshCemuProcess(Process p = null)
        {
            if (p == null)
            {
                try
                {
                    p = Process.Start(cemuPath ?? (Prompt.ShowDialog("Cemu path", "Enter the path to cemu.")));
                    cemuPath = p.StartInfo.FileName;
                    File.WriteAllText(Path.Combine(Path.Combine(Path.Combine(cemuDir, "PARAMS"), "PATHNAME")), cemuPath);
                }
                catch (InvalidOperationException e)
                {
                    Console.WriteLine($"Failed to get process!\n{e.Message}");
                    cemuProcess = null;
                    return;
                }
            }


            cemuProcess = p;

            if (cemuProcess != null)
            {
                cemuProcess.EnableRaisingEvents = true;
                cemuProcess.Exited += (o, e) =>
                {
                    cemuProcess = null;
                };
            }
        }

        private void ScanCemu()
        {
            Process p = getCemuProcess();

            if (state == CemuState.UNFOUND && p != null)
            {
                state = CemuState.RUNNING;
            }
            else if (
                state != CemuState.UNFOUND &&
                state != CemuState.GAMELOADED &&
                state != CemuState.READY &&
                p == null)
            {
                state = CemuState.UNFOUND;
                //DisableInterface();
            }

        }

        private bool FetchBaseInfoFromCemuProcess()
        {
            ///
            ///Fetching Game info from cemu process window title
            ///
            currentSession.cemuExeFile = new FileInfo(cemuPath);


            return true;
        }

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);

        private const int WM_CLOSE = 0x0010;
        private const int WM_DESTROY = 0x0011;
        private const int WM_QUIT = 0x0012;
        internal void KillCemuProcess(bool graceful)
        {
            if (graceful)
            {
                var cemus = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(currentSession.cemuExeFile.FullName));
                MessageBox.Show("Closing Cemu to configure the loaded game for FileStub.\n\n" +
                                "IF YOU OPENED ANY MENUS WHILE THE GAME WAS LOADING, AN ERROR MAY OCCUR. If an error occurs, try again. If it keeps occurring, poke the RTC devs.\n\n" +
                                "If Cemu doesn't close, quit it yourself to continue.",
                        "Registering Game for FileStub",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly);
                foreach (var p in cemus)
                {
                    try
                    {
                        var children = WindowHandleInfo.GetAllChildHandles(p.MainWindowHandle);
                        if (children != null)
                        {
                            foreach (var h in children)
                            {
                                SendMessage(h, WM_CLOSE, new IntPtr(0), new IntPtr(0));
                            }
                        }
                        SendMessage(p.MainWindowHandle, WM_CLOSE, new IntPtr(0), new IntPtr(0));
                        p.CloseMainWindow();
                        p.WaitForExit();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
            else
            {
                var p = cemuProcess;
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "taskkill";
                    psi.Arguments = $"/F /IM {currentSession.cemuExeFile.Name} /T";
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;

                    Process _p = new Process();
                    _p.OutputDataReceived += (sender, args) => Console.WriteLine("received output: {0}", args.Data);
                    _p.ErrorDataReceived += (sender, args) => Console.WriteLine("received error: {0}", args.Data);
                    _p.StartInfo = psi;
                    _p.Start();
                    _p.BeginOutputReadLine();
                }
                if (p == null)
                    System.Threading.Thread.Sleep(300); //Sleep for 300ms in case there's a cemu process we don't have a handle to
                else
                {
                    p.WaitForExit();
                }
            }
        }
        private bool LoadDataFromCemuFilesXml()
        {

            var directorypath = currentSession.cemuExeFile.DirectoryName;
            XmlSerializer xml = new XmlSerializer(typeof(Title_list));
            TextReader reader = new StreamReader(Path.Combine(directorypath, "title_list_cache.xml"));

            var titlelist = (Title_list)xml.Deserialize(reader);
            ///
            ///gathering data from log.txt and settings.xml files
            ///
            Dictionary<string, string> gameUpdatePaths = new Dictionary<string, string>();
            foreach (var title in titlelist.Title)
            {
                var knowngame = new CemuStubSession();
                if (title.App_type.ToLower() == "0800000e")
                {
                    continue;// ignore DLC
                }
                if (title.App_type.ToLower() == "0800001b")
                {
                    gameUpdatePaths[title.Name] = title.Path;
                    continue;
                }
                knowngame.cemuExeFile = currentSession.cemuExeFile;
                knowngame.FirstID = title.TitleId.Substring(0, 8);
                knowngame.SecondID = title.TitleId.Substring(8, 8);
                knowngame.gameName = title.Name;
                knowngame.gameRpxPath = Directory.GetFiles(Path.Combine(title.Path, "code")).FirstOrDefault(x => x.EndsWith(".rpx"));
                knowngame.gameRpxFileInfo = new FileInfo(knowngame.gameRpxPath);
                knowngame.updateCodePath = Directory.Exists(Path.Combine(directorypath, "mlc01", "usr", "title", knowngame.FirstID, knowngame.SecondID, "code")) ? Path.Combine(directorypath, "mlc01", "usr", "title", knowngame.FirstID, knowngame.SecondID, "code") : (gameUpdatePaths.ContainsKey(title.Name)) ? Path.Combine(gameUpdatePaths[title.Name], "code") : Path.Combine(title.Path, "code");
                knowngame.updateRpxPath = knowngame.updateCodePath;
                knowngame.updateMetaPath = Directory.Exists(Path.Combine(directorypath, "mlc01", "usr", "title", knowngame.FirstID, knowngame.SecondID, "meta")) ? Path.Combine(directorypath, "mlc01", "usr", "title", knowngame.FirstID, knowngame.SecondID, "meta") : (gameUpdatePaths.ContainsKey(title.Name)) ? Path.Combine(gameUpdatePaths[title.Name], "meta") : Path.Combine(title.Path, "meta");
                knowngame.gameSaveFolder = new DirectoryInfo(Path.Combine(directorypath, "mlc01", "usr", "save", knowngame.FirstID, knowngame.SecondID));
                knowngame.rpxFile = knowngame.gameRpxFileInfo.Name;
                knowngame.updateRpxLocation = Path.Combine(knowngame.updateCodePath, knowngame.gameRpxFileInfo.Name);
                knowngame.updateRpxCompressed = Path.Combine(knowngame.updateCodePath,
                    "compressed_" + currentSession.rpxFile);
                knowngame.updateRpxBackup =
                    Path.Combine(knowngame.updateCodePath, "backup_" + knowngame.rpxFile);
                knowngame.updateRpxUncompressedToken =
                    Path.Combine(knowngame.updateCodePath, "UNCOMPRESSED.txt");
                knownGamesDico[knowngame.gameName] = knowngame;
            }

            return true;
        }

        private bool LoadDataFromCemuFilesBin()
        {
            ///
            ///gathering data from log.txt and settings.xml files
            ///

            string[] logTxt = File.ReadAllLines(Path.Combine(currentSession.cemuExeFile.DirectoryName, "log.txt"));
            //string[] settingsXml = File.ReadAllLines(Path.Combine(cemuExeFile.DirectoryName, "settings.xml"));
            byte[] settingsBin = File.ReadAllBytes(Path.Combine(currentSession.cemuExeFile.DirectoryName, "settings.bin"));

            //getting rpx filename from log.txt
            string logLoadingLine = logTxt.FirstOrDefault(it => it.Contains("Loading") && it.Contains(".rpx"));

            if (String.IsNullOrWhiteSpace(logLoadingLine))
            {
                MessageBox.Show(
                    "Could not find an rpx file to corrupt.\n\n" +
                    "If the game you are trying to corrupt is in Wud format, you must extract it for it to be corruptible\n\n" +
                    "Loading aborted.", "Error finding game");
                state = CemuState.UNFOUND;
                return false;
            }

            string[] logLoadingLineParts = logLoadingLine.Split(' ');
            currentSession.rpxFile = logLoadingLineParts[logLoadingLineParts.Length - 1];

            //Getting rpx path from settings.bin
            byte[] rpx = { 0x2E, 0x00, 0x72, 0x00, 0x70, 0x00, 0x78, 0x00 }; //".rpx" encoded as utf-16
            int startOffset = 0xB7;
            var endOffset = Array.IndexOf(settingsBin, rpx) + rpx.Length;



            byte[] tmp = new byte[endOffset - startOffset];
            Array.Copy(settingsBin, startOffset, tmp, 0, endOffset - startOffset);
            var gamePath = Encoding.Unicode.GetString(tmp);

            try
            {
                if (File.Exists(gamePath))
                {
                    Console.WriteLine("Found game " + gamePath);
                }
                else
                {
                    throw new Exception("Couldn't find RPX");
                }
            }
            catch (Exception e)
            {
                _ = e;

                MessageBox.Show("Something went wrong when locating the RPX of the running game.\nYou can probably fix this by going to your Cemu folder and deleting settings.bin, then trying again.\nIf this doesn't fix it, poke the devs.\n\nCouldn't find: " + gamePath);
                state = CemuState.UNFOUND;
                return false;
            }


            currentSession.gameRpxPath = gamePath;
            currentSession.gameRpxFileInfo = new FileInfo(currentSession.gameRpxPath);
            currentSession.updateRpxPath = Path.Combine(currentSession.cemuExeFile.DirectoryName, "mlc01", "usr", "title", currentSession.FirstID, currentSession.SecondID);

            currentSession.updateCodePath = Path.Combine(currentSession.updateRpxPath, "code");
            currentSession.updateMetaPath = Path.Combine(currentSession.updateRpxPath, "meta");

            currentSession.gameSaveFolder = new DirectoryInfo(Path.Combine(currentSession.cemuExeFile.DirectoryName, "mlc01", "usr", "save", currentSession.FirstID, currentSession.SecondID));



            currentSession.updateRpxLocation = Path.Combine(currentSession.updateCodePath, currentSession.rpxFile);
            currentSession.updateRpxCompressed = Path.Combine(currentSession.updateCodePath, "compressed_" + currentSession.rpxFile);
            currentSession.updateRpxBackup = Path.Combine(currentSession.updateCodePath, "backup_" + currentSession.rpxFile);
            currentSession.updateRpxUncompressedToken = Path.Combine(currentSession.updateCodePath, "UNCOMPRESSED.txt");

            return true;
        }


        public bool LoadKnownGames()
        {
            JsonSerializer serializer = new JsonSerializer();
            string path = Path.Combine(cemuDir, "PARAMS", "knowngames.json");
            if (!File.Exists(path))
            {
                knownGamesDico = new Dictionary<string, CemuStubSession>();
                return true;
            }
            try
            {

                using (StreamReader sw = new StreamReader(path))
                using (JsonTextReader reader = new JsonTextReader(sw))
                {
                    knownGamesDico = serializer.Deserialize<Dictionary<string, CemuStubSession>>(reader);
                }

                foreach (var key in knownGamesDico.Keys)
                    cbSelectedGame.Items.Add(key);
            }
            catch (IOException e)
            {
                MessageBox.Show("Unable to access the filemap! Figure out what's locking it and then restart the WGH.\n" + e.ToString());
                return false;
            }
            return true;
        }
        public bool SaveKnownGames()
        {
            JsonSerializer serializer = new JsonSerializer();
            var path = Path.Combine(cemuDir, "PARAMS", "knowngames.json");
            try
            {
                using (StreamWriter sw = new StreamWriter(path))
                using (JsonWriter writer = new JsonTextWriter(sw))
                {
                    serializer.Serialize(writer, knownGamesDico);
                }
            }
            catch (IOException e)
            {
                MessageBox.Show("Unable to access the known games!\n" + e.ToString());
                return false;
            }
            return true;
        }

        //private static bool LoadRpxFileInterface()
        //{
        //    try
        //    {
        //        currentSession.fileInterfaceTargetId = "File|" + currentSession.updateRpxLocation;

        //        var ft = new FileTarget(currentSession.gameRpxPath, null);

        //        rpxInterface = new FileInterface(ft);
        //        rpxInterface.getMemoryDump();
        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine(ex);

        //        if (ex is FileNotFoundException && knownGamesDico.ContainsKey(currentSession.gameName))
        //        {
        //            object selectedItem = cbSelectedGame.SelectedItem;
        //            cbSelectedGame.SelectedIndex = 0;

        //            if (MessageBox.Show($"Do you want to remove the entry for {selectedItem}?", "Error lading rpx file", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        //            {
        //                cbSelectedGame.Items.Remove(selectedItem);
        //                knownGamesDico.Remove(selectedItem.ToString());
        //                SaveKnownGames();
        //            }

        //        }
        //        else
        //        {
        //            cbSelectedGame.SelectedIndex = 0;
        //        }
        //        return false;

        //    }
        //}

        internal string PrepareUpdateFolder(bool overwrite = false)
        {
            if (overwrite)
                if (Directory.Exists(currentSession.updateRpxPath))
                    Directory.Delete(currentSession.updateRpxPath, true);


            //Creating fake update if update doesn't already exist
            if (!Directory.Exists(currentSession.updateRpxPath) || !File.Exists(currentSession.updateRpxLocation))
            {
                Directory.CreateDirectory(currentSession.updateRpxPath);
                Directory.CreateDirectory(currentSession.updateCodePath);
                Directory.CreateDirectory(currentSession.updateMetaPath);

                foreach (var file in currentSession.gameRpxFileInfo.Directory.GetFiles())
                    File.Copy(file.FullName, Path.Combine(currentSession.updateCodePath, file.Name), true);

                DirectoryInfo gameDirectoryInfo = currentSession.gameRpxFileInfo.Directory.Parent;
                DirectoryInfo metaDirectoryInfo = new DirectoryInfo(currentSession.updateMetaPath);

                foreach (var file in metaDirectoryInfo.GetFiles())
                    File.Copy(file.FullName, currentSession.updateMetaPath);

            }

            //Uncompress update rpx if it isn't already

            DirectoryInfo updateCodeDirectoryInfo = new DirectoryInfo(currentSession.updateCodePath);
            currentSession.updateCodeFiles = updateCodeDirectoryInfo.GetFiles();

            if (!File.Exists(currentSession.updateRpxUncompressedToken))
            {
                if (File.Exists(currentSession.updateRpxCompressed))
                    File.Delete(currentSession.updateRpxLocation);
                else
                    File.Move(currentSession.updateRpxLocation, currentSession.updateRpxCompressed);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(cemuDir, "wiiurpxtool.exe");
                psi.WorkingDirectory = cemuDir;
                psi.Arguments = $"-d \"{currentSession.updateRpxCompressed}\" \"{currentSession.updateRpxLocation}\"";
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);

                p.WaitForExit();

                File.WriteAllText(currentSession.updateRpxUncompressedToken, "DONE");
            }

            return currentSession.updateRpxLocation;
        }

        internal void UnmodGame()
        {
            KillCemuProcess(false);

            //remove item from known games and go back to autodetect
            var lastRef = currentSession;

            //remove fake update from game
            if (File.Exists(lastRef.updateRpxCompressed))
            {
                if (File.Exists(lastRef.updateRpxLocation))
                    File.Delete(lastRef.updateRpxLocation);

                if (File.Exists(lastRef.updateRpxCompressed))
                {
                    File.Copy(lastRef.updateRpxCompressed, lastRef.updateRpxLocation);
                    File.Delete(lastRef.updateRpxCompressed);
                }

                if (File.Exists(lastRef.updateRpxBackup))
                    File.Delete(lastRef.updateRpxBackup);

                if (File.Exists(lastRef.updateRpxUncompressedToken))
                    File.Delete(lastRef.updateRpxUncompressedToken);
            }
            else if (Directory.Exists(lastRef.updateRpxPath))
                Directory.Delete(lastRef.updateRpxPath, true);

            FileInterface.CompositeFilenameDico.Remove(lastRef.gameName);
            knownGamesDico.Remove(lastRef.gameName);
            SaveKnownGames();
            cbSelectedGame.SelectedIndex = 0;
            cbSelectedGame.Items.Remove(lastRef.gameName);
        }


        internal bool SelectGame(string selected = null)
        {
            if (selected != null && selected != "Autodetect")
                currentSession = knownGamesDico[selected];

            if (selected == null)
            {
                selected = Prompt.ShowDialog("Select game", "Enter the name of the game you wish to corrupt.");
                while (!knownGamesDico.ContainsKey(selected))
                {
                    selected = Prompt.ShowDialog("Select game", "INVALID GAME NAME.\nEnter the name of the game you wish to corrupt.");
                }
                currentSession = knownGamesDico[selected];
            }

            var cemuFullPath = currentSession.cemuExeFile;
            if (!File.Exists(cemuFullPath.FullName))
            {
                //Cemu could not be found. Prompt a message for replacement, a browse box, and replace all refs for the known games

                string message = "FileStub couldn't find Cemu emulator. Would you like to specify a new location?";
                var result = MessageBox.Show(message, "Error finding cemu", MessageBoxButtons.YesNo, MessageBoxIcon.Error);

                string cemuLocation = null;
                if (result == DialogResult.Yes)
                {
                    OpenFileDialog ofd = new OpenFileDialog
                    {
                        DefaultExt = "exe",
                        Title = "Open Cemu Emulator",
                        Filter = "Cemu Emulator|*.exe",
                        RestoreDirectory = true
                    };
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        cemuLocation = ofd.FileName;
                    }
                    else
                    {
                        cbSelectedGame.SelectedIndex = 0;
                        return false;
                    }

                    currentSession.cemuExeFile = new FileInfo(cemuLocation);
                    foreach (CemuStubSession cgi in knownGamesDico.Values)
                        cgi.cemuExeFile = currentSession.cemuExeFile;
                    SaveKnownGames();

                }
                else
                {
                    cbSelectedGame.SelectedIndex = 0;
                    return false;
                }
            }

            string rpxFullPath = currentSession.gameRpxFileInfo.FullName;
            if (!File.Exists(rpxFullPath))
            {
                string message = "File Stub couldn't find the Rpx file for this game. Would you like to remove this entry?";
                var result = MessageBox.Show(message, "Error finding game", MessageBoxButtons.YesNo, MessageBoxIcon.Error);

                if (result == DialogResult.Yes)
                    UnmodGame();

                cbSelectedGame.SelectedIndex = 0;
                return false;
            }

            //if (!LoadRpxFileInterface())
            //    return false;



            //load target here




            state = CemuState.READY;
            //S.GET<StubForm>().lbCemuStatus.Text = "Ready for corrupting";
            //S.GET<StubForm>().lbTargetedGameRpx.Text = currentSession.gameRpxFileInfo.FullName;
            //S.GET<StubForm>().lbTargetedGameId.Text = "Game ID: " + currentSession.FirstID + "-" + currentSession.SecondID;
            //EnableInterface();

            return true;
        }


        private string SearchForCemuInstance()
        {

            ScanCemu();

            if (true)
            {
                state = CemuState.PREPARING; // this prevents the ticker to call this method again

                //Game is loaded in cemu, let's gather all the info we need


                if (!FetchBaseInfoFromCemuProcess())
                {
                    return null; //Couldn't fetch the correct info, or they were in online mode
                }


                KillCemuProcess(true);

                if (!LoadDataFromCemuFilesXml())
                {
                    MessageBox.Show("Failed to get RPX file location from Cemu.\nIf you continue to see this error, let the RTC Devs know.");
                    return null; //Could not get the rpx file location
                }

                //knownGamesDico[currentSession.gameName] = currentSession;

                if (!SelectGame())
                    return null;

                // Prepare fake update and backup
                var rpxTargetFile = PrepareUpdateFolder();

                DontSelectGame = true;
                cbSelectedGame.Items.Add(currentSession.gameName);
                cbSelectedGame.SelectedIndex = cbSelectedGame.Items.Count - 1;
                DontSelectGame = false;

                foreach (CemuStubSession cgi in knownGamesDico.Values)
                    cgi.cemuExeFile = currentSession.cemuExeFile;

                SaveKnownGames();

                return rpxTargetFile;

            }

            return null;

        }


        public void BrowseFiles()
        {
            //NOT SUPPORTED
        }

        private void FileStubTemplateCemu_Load(object sender, EventArgs e)
        {
            cbSelectedGame.SelectedIndex = 0;
            LoadKnownGames();
        }

        private void cbSelectedGame_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (DontSelectGame)
                return;

            var selected = cbSelectedGame.SelectedItem.ToString();

            if (selected == "Autodetect")
                return;

            if (!SelectGame(selected))
            {
                cbSelectedGame.SelectedIndex = 0;
                return;
            }

            S.GET<StubForm>().btnLoadTargets_Click(null, null);

        }

        private void btnGetSegments_Click(object sender, EventArgs e)
        {
            foreach (var fi in (FileWatch.currentSession.fileInterface as MultipleFileInterface).FileInterfaces)
                GetSegments(fi);
        }

    }

    enum CemuState
    {
        UNFOUND,
        RUNNING,
        GAMELOADED,
        PREPARING,
        READY
    }

    public class CemuStubSession
    {
        public FileInfo gameRpxFileInfo = null;
        public FileInfo cemuExeFile = null;
        public FileInfo[] updateCodeFiles = null;
        public DirectoryInfo gameSaveFolder = null;
        public string rpxFile = null;
        public string gameRpxPath = null;
        public string updateRpxPath = null;
        public string updateCodePath = null;
        public string updateMetaPath = null;
        public string updateRpxLocation = null;
        public string updateRpxCompressed = null;
        public string updateRpxBackup = null;
        public string FirstID = null;
        public string SecondID = null;
        public string fileInterfaceTargetId = null;
        public string gameName = "Autodetect";
        public string updateRpxUncompressedToken = null;
        public FileInterface rpxInterface = null;
        //internal FileMemoryInterface fileInterface;

        public override string ToString()
        {
            return gameName;
        }
    }

    public static class WindowHandleInfo
    {
        private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr window, EnumWindowProc callback, IntPtr lParam);

        public static List<IntPtr> GetAllChildHandles(IntPtr MainHandle)
        {
            List<IntPtr> childHandles = new List<IntPtr>();

            GCHandle gcChildhandlesList = GCHandle.Alloc(childHandles);
            IntPtr pointerChildHandlesList = GCHandle.ToIntPtr(gcChildhandlesList);

            try
            {
                EnumWindowProc childProc = new EnumWindowProc(EnumWindow);
                EnumChildWindows(MainHandle, childProc, pointerChildHandlesList);
            }
            finally
            {
                gcChildhandlesList.Free();
            }

            return childHandles;
        }

        private static bool EnumWindow(IntPtr hWnd, IntPtr lParam)
        {
            GCHandle gcChildhandlesList = GCHandle.FromIntPtr(lParam);

            if (gcChildhandlesList == null || gcChildhandlesList.Target == null)
            {
                return false;
            }

            List<IntPtr> childHandles = gcChildhandlesList.Target as List<IntPtr>;
            childHandles.Add(hWnd);

            return true;
        }
    }
    [XmlRoot(ElementName = "title")]
    public class Title
    {
        [XmlElement(ElementName = "region")]
        public string Region { get; set; }
        [XmlElement(ElementName = "name")]
        public string Name { get; set; }
        [XmlElement(ElementName = "format")]
        public string Format { get; set; }
        [XmlElement(ElementName = "path")]
        public string Path { get; set; }
        [XmlAttribute(AttributeName = "titleId")]
        public string TitleId { get; set; }
        [XmlAttribute(AttributeName = "version")]
        public string Version { get; set; }
        [XmlAttribute(AttributeName = "group_id")]
        public string Group_id { get; set; }
        [XmlAttribute(AttributeName = "app_type")]
        public string App_type { get; set; }
    }

    [XmlRoot(ElementName = "title_list")]
    public class Title_list
    {
        [XmlElement(ElementName = "title")]
        public List<Title> Title { get; set; }
    }
}
