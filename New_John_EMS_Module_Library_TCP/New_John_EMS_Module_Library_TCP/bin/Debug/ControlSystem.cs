using System;
using Crestron.SimplSharp;                              // For Basic SIMPL# Classes
using Crestron.SimplSharpPro;                           // For Basic SIMPL#Pro classes
using Crestron.SimplSharpPro.CrestronThread;            // For Threading
using Crestron.SimplSharpPro.UI;
using New_John_EMS_Library;
using BiampLibrary;
using Crestron.SimplSharpPro.DeviceSupport;
using Crestron.SimplSharp.CrestronIO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Thread = System.Threading.Thread;
using System.Linq;

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EMS_Module {
    public class ControlSystem : CrestronControlSystem {

        ComPort.ComPortSpec comSpec;
        Boolean emsDebugState = true;

        // Devices
        Ts1070 TP1;
        Biamp biamp;
        BoolInputSig TP1_Subpage;
        EMS_Camera[] Camera = Enumerable.Repeat(new EMS_Camera("0.0.0.0"), 200).ToArray();
        EMS_Room[] Room = Enumerable.Repeat(new EMS_Room(""), 200).ToArray();

        // Variables
        string[] biamp_IP = { "0.0.0.0", "0.0.0.0" };
        string ems_ip = "0.0.0.0";
        int ems_port = 0;
        string username = "admin";
        string password = "password";
        int biamp_matrix_quantity = 1;
        int biamp_matrix_offset = 0;
        int player_offset = 0;
        int play_delay = 50;
        string paging_keyword = "Spk";
        string[] Camera_Label = new string[200];
        string[] Room_Label = new string[100];
        int total_rooms = 0;
        int TP1_Camera, Room_Label_Index = 0;
        Dictionary<int, BiampMute> muteMap = new Dictionary<int, BiampMute>();
        Dictionary<(int,int), BiampCrosspoint> crosspointMap = new Dictionary<(int, int), BiampCrosspoint>();

        // Biamp objects
        BiampMute biampMute;
        BiampCrosspoint biampCrosspoint;

        // Threading
        Thread EMS_Load_Config_Thread = null;

        public enum SmartObjectID {
            pageSelect = 10,
            camPreset = 74,
            camDPad = 72,
            camZoom = 73,
            camSelect = 70,
            roomName = 45,
            roomNameCameraSel = 69,
            roomNameRecordingSel = 68
        }

        /// <summary>
        /// ControlSystem Constructor. Starting point for the SIMPL#Pro program.
        /// Use the constructor to:
        /// * Initialize the maximum number of threads (max = 400)
        /// * Register devices
        /// * Register event handlers
        /// * Add Console Commands
        /// 
        /// Please be aware that the constructor needs to exit quickly; if it doesn't
        /// exit in time, the SIMPL#Pro program will exit.
        /// 
        /// You cannot send / receive data in the constructor
        /// </summary>
        public ControlSystem() : base() {

            try {

                // Loading our EMS Config sooner, not in init system.  So we can create a biamp object.
                EMS_Load_Config("");

                Crestron.SimplSharpPro.CrestronThread.Thread.MaxNumberOfUserThreads = 80;

                //Subscribe to the controller events (System, Program, and Ethernet)
                CrestronEnvironment.SystemEventHandler += new SystemEventHandler(_ControllerSystemEventHandler);
                CrestronEnvironment.ProgramStatusEventHandler += new ProgramStatusEventHandler(_ControllerProgramEventHandler);
                CrestronEnvironment.EthernetEventHandler += new EthernetEventHandler(_ControllerEthernetEventHandler);

                // Message Player Events
                EMS_Modules.Message_Play += new messagePlayerHandler(Play_Message);
                EMS_Modules.Message_Record += new messagePlayerHandler(Record_Message);
                EMS_Modules.Message_Stop += new messagePlayerHandler(Stop_Message);
                EMS_Modules.Request_Ping += new requestHandler(Request_Ping_Func);

                // Biamp Related Events
                EMS_Modules.Biamp_Update += new biampRouteHandler(Update_Biamp);
                EMS_Modules.Biamp_Preset += new biampPresetHandler(Preset_Biamp);
                EMS_Modules.Biamp_Mute_Update += new biampMuteHandler(Mute_Update);

                EMS_Modules.TCP_Server_Receive_Data += new tcpDataHandler(TCP_Server_Receive_Data);
                EMS_Modules.EMS_Client_Receive_Data += new tcpDataHandler(TCP_Client_Receive_Data);
                EMS_Modules.Debug_Print += new New_John_EMS_Library.debugHandler(Debug_Print);
                EMS_Camera.TCP_Client_Send += new tcpDataSend(TCP_Client_Send);
                EMS_Camera.Preset_Saved += new cameraControl(Cam_Preset_Saved);
                EMS_Room.TCP_Client_Send += new tcpDataSend(TCP_Client_Send);
                
                //EMS_Modules.EmsRequestStatPrivEvent += new emsToCrestronStatusRequest(EmsRequestStatusPrivacy);
                EMS_Modules.EmsRequestMuteEvent += new emsToCrestronMuteRequest(EmsRequestMute);
                EMS_Modules.Set_Room_States_Event += new set_Room_States_Handler(Set_Room_States_Func);
                EMS_Modules.Update_TouchPanel_FeedBack_Status += new Update_TouchPanel_Status_FB_Handler(Update_TouchPanel_FeedBack_Status);
                EMS_Modules.Update_TouchPanel_FeedBack_Mute += new Update_TouchPanel_Mute_FB_Handler(Update_TouchPanel_FeedBack_Mute);


                // Biamp Library Events
                BiampMute.Debug_Print += new debugHandler2(Debug_Print);
                BiampCrosspoint.Debug_Print += new debugHandler2(Debug_Print);

                //Create custom console commands to control the processor
                CrestronConsole.AddNewConsoleCommand(EMSDebug, "emsdebug", "on or off to debug EMS signals", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(TCP_Client_Send, "csend", "send a command to the tcp client", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(TCP_Server_Send, "ssend", "send a command to the tcp server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(TCP_Server_Stop, "sstop", "close our tcp server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(EMS_Load_Config, "loadConfig", "pull config data from csv file", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(EMS_Change_IP, "emsIP", "Set new ip address for the ems server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(Biamp_Change_IP, "biampIP", "Set new ip address for the biamp processor ssh connection", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(BiampClientDisconnect, "discBC", "disconnect our Biamp Library client from the Biamp server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(BiampClientReconnect, "recBC", "reconnect our Biamp Library client from the Biamp server and start threads", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(BiampClientConnectStatus, "statusBC", "check if our Biamp Library client is connected to Biamp", ConsoleAccessLevelEnum.AccessAdministrator);

                EMS_Modules.Declare_Constants();

                if (this.SupportsEthernet) {
                    TP1 = new Ts1070(0x10, this);
                    TP1.SigChange += Touchpanel_SigChange;
                    TP1.OnlineStatusChange += Touchpanel_StatusChange;

                    string filePath = Path.Combine(Directory.GetApplicationDirectory(), "EMS TS-1070 Template.sgd");

                    if (TP1.Register() != eDeviceRegistrationUnRegistrationResponse.Success) {
                        ErrorLog.Error("Error Registering panel 1: {0}", TP1.RegistrationFailureReason);
                    } else {
                        if (File.Exists(filePath)) {
                            TP1.LoadSmartObjects(filePath);
                            foreach (KeyValuePair<uint, SmartObject> pair in TP1.SmartObjects) {
                                pair.Value.SigChange += SmartObject_SigChange;
                            }
                        }
                    }
                } else {
                    ErrorLog.Error("Processor does not support Ethernet");
                }

                if (this.SupportsComPort) {
                    comSpec = new ComPort.ComPortSpec();
                    comSpec.BaudRate = ComPort.eComBaudRates.ComspecBaudRate9600;
                    comSpec.Protocol = ComPort.eComProtocolType.ComspecProtocolRS232;

                    foreach (ComPort port in this.ComPorts) {
                        if (port.Register() != eDeviceRegistrationUnRegistrationResponse.Success) {
                            ErrorLog.Error("Processor failed to configure COM ports");
                        } else {
                            port.SetComPortSpec(comSpec);
                            port.SerialDataReceived += new ComPortDataReceivedEvent(ComPort_Receive_Data);
                        }
                    }
                } else {
                    ErrorLog.Error("Processor does not support COM ports");
                }
            } catch (Exception e) {
                ErrorLog.Error("Error in the constructor: {0}", e.Message);
            }
            ErrorLog.Notice("Finished constructor");

        }

        /// <summary>
        /// InitializeSystem - this method gets called after the constructor 
        /// has finished. 
        /// 
        /// Use InitializeSystem to:
        /// * Start threads
        /// * Configure ports, such as serial and verisports
        /// * Start and initialize socket connections
        /// Send initial device configurations
        /// 
        /// Please be aware that InitializeSystem needs to exit quickly also; 
        /// if it doesn't exit in time, the SIMPL#Pro program will exit.
        /// </summary>
        public override void InitializeSystem() {
            try {
                if (this.SupportsEthernet) {
                    CrestronEthernetHelper.GetEthernetParameter(CrestronEthernetHelper.ETHERNET_PARAMETER_TO_GET.GET_CURRENT_IP_ADDRESS, CrestronEthernetHelper.GetAdapterdIdForSpecifiedAdapterType(EthernetAdapterType.EthernetLANAdapter));

                    // Do sstop console command to close below server pocket.
                    // Side note, if make ip address all 0s, it will still work if switch our processor ip
                    EMS_Modules.TCP_Server_Start("192.168.254.1", 53124);

                } else {
                    ErrorLog.Error("Processor does not support Ethernet");
                }
            } catch (Exception e) {
                ErrorLog.Error("Error in InitializeSystem: {0}", e.Message);
            }
            ErrorLog.Notice("Finished initialize");
        }

        void TCP_Server_Stop(string args) {
            if (emsDebugState) CrestronConsole.PrintLine("TCP Server Stop");
            EMS_Modules.TCP_Server_Stop();
        }

        void TCP_Server_Send(string args) {
            if (emsDebugState) CrestronConsole.PrintLine("TCP Sending: {0}", args);
            EMS_Modules.TCP_Server_Send(args);
        }

        void TCP_Client_Send(string args) {
            if (args.Length > 0) {
                if (emsDebugState) CrestronConsole.PrintLine("TCP Sending: {0} to ems server ip and port {1} {2}", args, ems_ip, ems_port);
                // We are client, sending to EMS server.
                EMS_Modules.EMS_Client_Send(args, ems_ip, ems_port);
                //try {
                //  TcpClient EMS_Client = new TcpClient();
                //EMS_Client.Connect("192.168.254.59", 53125);
                //CrestronConsole.PrintLine("Connection attempted");
                //using (var stream = EMS_Client.GetStream()) {
                //   CrestronConsole.PrintLine("We got stream.");
                // byte[] data = System.Text.Encoding.ASCII.GetBytes("Hello Hercules");
                //stream.Write(data, 0, data.Length);
                //CrestronConsole.PrintLine("Data sent.");
                //}
                //CrestronConsole.PrintLine("Connection Successful");
                //} catch (Exception ex) {
                //  CrestronConsole.PrintLine($"Connection failed: {ex.Message}");
                //}
            }
        }

        // Console Commands
        void EMS_Change_IP(string args) {
            string[] argList = args.Split(' ');
            if (args.Length > 0) {
                Debug_Print($"Changed EMS server ip from {ems_ip} to {args}");
                ems_ip = args;
                EMS_Modules.EMS_Client_Disconnect();
            } else {
                CrestronConsole.PrintLine("EMS server ip: {0}", ems_ip);
            }
        }
        private void BiampClientDisconnect(string cmdParameters) {
            biamp.Disconnect();
        }
        void BiampClientReconnect(string cmdParameters) {
            // Reconnect our biamp library client to the biamp, the shell, and start data threads for Biamp.
            biamp.ConnectAndStartThreads();
        }
        void BiampClientConnectStatus(string cmdParameters) {
            CrestronConsole.PrintLine(biamp.isConnected().ToString());
        }
        void Biamp_Change_IP(string args) {
            string[] argList = args.Split(' ');
            if (args.Length > 0) {
                Debug_Print($"Changed EMS server ip from {ems_ip} to {args}");
                biamp_IP[1] = args;
                biamp = new Biamp(biamp_IP[0], username, password);
            } else {
                CrestronConsole.PrintLine("EMS server ip: {0}", ems_ip);
            }
        }

        void EMSDebug(string args) {// Command "emsdebug" + "on"/"off"
            string[] argList = args.Split(' ');
            if (args.Length > 0) {
                if (argList[0] == "on") {
                    emsDebugState = true;
                } else if (argList[0] == "off") {
                    emsDebugState = false;
                }
                CrestronConsole.PrintLine("EMS Debug State: {0}", emsDebugState ? "on" : "off");
            } else {
                CrestronConsole.PrintLine("EMS Debug State: {0}", emsDebugState ? "on" : "off");
            }
        }

        bool EMS_Load_Config_Handler(string args) {

            string[] argList = args.Split(' ');
            string fileName = "EMS_Config.csv";

            if (args.Length == 1) {
                // New File
                fileName = argList[0];
            } else {
                // Default File
                if (CrestronEnvironment.DevicePlatform == eDevicePlatform.Appliance) {
                    // Physical Device like CP4
                    fileName = "\\User\\EMS_Config.csv";
                } else if (CrestronEnvironment.DevicePlatform == eDevicePlatform.Server) {
                    // Crestron VC4
                }
            }

            Debug_Print($"Loading Config from file: {fileName}");

            string line;
            StreamReader streamReader = null;
            try {
                streamReader = new StreamReader(fileName);
            } catch (Exception e) {
                ErrorLog.Error($"Config file does not exis: {fileName}");
                return false;
            }

            if (File.Exists(fileName)) {
                try {
                    while ((line = streamReader.ReadLine()) != null) {
                        if (line.Contains("Server_IP")) {
                            ems_ip = line.Split(new[] { ',' }, 2)[1];
                        } else if (line.Contains("Server_Port")) {
                            ems_port = Int32.Parse(Regex.Match(line.Split(new[] { ',' }, 2)[1], @"\d+").Value);
                        } else if (line.Contains("Biamp_IP[")) {
                            int temp = Int32.Parse(Regex.Match(line, @"\d+").Value) - 1;
                            biamp_IP[temp] = line.Split(new[] { ',' }, 2)[1];
                        } else if (line.Contains("Paging_Matrix_Quantity,")) {
                            biamp_matrix_quantity = Int32.Parse(Regex.Match(line.Split(new[] { ',' }, 2)[1], @"\d+").Value);
                        } else if (line.Contains("Paging_Matrix_Offset,")) {
                            biamp_matrix_offset = Int32.Parse(Regex.Match(line.Split(new[] { ',' }, 2)[1], @"\d+").Value);
                        } else if (line.Contains("Player_Offset,")) {
                            player_offset = Int32.Parse(Regex.Match(line.Split(new[] { ',' }, 2)[1], @"\d+").Value);
                        } else if (line.Contains("Play_Delay,")) {
                            play_delay = Int32.Parse(Regex.Match(line.Split(new[] { ',' }, 2)[1], @"\d+").Value);
                        } else if (line.Contains("Paging_Keyword,")) {
                            paging_keyword = line.Split(new[] { ',' }, 2)[1];
                        } else if (line.Contains("Camera_IP[")) {
                            int temp = Int32.Parse(Regex.Match(line, @"\d+").Value) - 1;
                            Camera[temp] = new EMS_Camera(line.Split(new[] { ',' }, 2)[1]);
                            // OHHH, maybe read in as col1, col2! from the excel.  so [1] is the value!!
                            // "Camera_IP[4],12.1.2.1"  
                        } else if (line.Contains("Camera_Label[")) {
                            int temp = Int32.Parse(Regex.Match(line, @"\d+").Value) - 1;
                            Camera_Label[temp] = line.Split(new[] { ',' }, 2)[1];
                        } else if (line.Contains("Room_ID[")) {
                            int temp = Int32.Parse(Regex.Match(line, @"\d+").Value) - 1;
                            Room[temp] = new EMS_Room(line.Split(new[] { ',' }, 2)[1]); // line.Split(',', 2) similar but our way if need many delimiters or other types
                            total_rooms += 1;                                             // the 2 saying split it into 2 parts
                        } else if (line.Contains("Room_Label[")) {
                            int temp = Int32.Parse(Regex.Match(line, @"\d+").Value) - 1;
                            Room_Label[temp] = line.Split(new[] { ',' }, 2)[1];
                        }
                    }
                } catch {
                    ErrorLog.Error("Error reading config file");
                    return false;
                }
            }

            biamp = new Biamp(biamp_IP[0], username, password);

            //why would i do biamp, think I should make these all Biamp class events
            // and move them to top of this code??
            Biamp.Debug_Print += new debugHandler2(Debug_Print);
            biamp.DataReceivedEvent += new Biamp.DataReceivedHandler(ReceiveData);
            biamp.ObjectFeedbackEvent += new Biamp.ObjectEventHandler(ObjectFeedback);

            // LINE below could be spawning a new thread under the hood (not the one I create). Thus,
            // when it runs in the .dll it might be a new thread, so when that crashes it propagates
            // back to this main code.  So handle exception here.
            try {
                biamp.ConnectAndStartThreads();
            } catch (Exception ex) {
                CrestronConsole.PrintLine($"Error in Connect and Start Threads call attempt: {ex.Message}");
            }
            return true;
        }

        void EMS_Load_Config(string args) {
            EMS_Load_Config_Thread = new Thread(() => EMS_Load_Config_Handler(args));
            EMS_Load_Config_Thread.Start();
        }

        // Event Handlers
        void Touchpanel_StatusChange(GenericBase currentDevice, OnlineOfflineEventArgs args) {

            try {
                //Object reference not set to the instance of an object error
                if (currentDevice == TP1) {
                    if (args.DeviceOnLine) {
                        CrestronConsole.PrintLine("TP1 has come Online");
                        TP1.BooleanInput[10].BoolValue = true;
                        TP1.BooleanInput[10].BoolValue = false;

                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].StringInput["Set Item 1 Text"].StringValue = "Paging";
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].BooleanInput["Item 1 Visible"].BoolValue = true;
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].StringInput["Set Item 3 Text"].StringValue = "Camera Control";
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].BooleanInput["Item 3 Visible"].BoolValue = true;
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].StringInput["Set Item 4 Text"].StringValue = "Camera Control Room";
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].BooleanInput["Item 4 Visible"].BoolValue = true;
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].StringInput["Set Item 2 Text"].StringValue = "Room Control";
                        TP1.SmartObjects[(uint)SmartObjectID.pageSelect].BooleanInput["Item 2 Visible"].BoolValue = true;

                        // Set Camera Labels of Camera Select
                        TP1_Camera = -1;
                        for (int i = 0; i < Camera.Length; i++) {
                            if (Camera[i].get_IP() != "0.0.0.0") {
                                TP1.SmartObjects[(uint)SmartObjectID.camSelect].StringInput[$"text-o{i + 1}"].StringValue = Camera_Label[i];
                                TP1.SmartObjects[(uint)SmartObjectID.camSelect].BooleanInput[$"Item {i + 1} Visible"].BoolValue = true;
                                if (TP1_Camera == -1) { // Just highlights Cam 1 Select at the start.
                                    TP1_Camera = i;
                                    TP1.SmartObjects[(uint)SmartObjectID.camSelect].BooleanInput[$"fb{TP1_Camera + 1}"].BoolValue = true;
                                }
                            }
                        }
                        // roomName Room Control labels
                        string label_value = "";
                        for (int i = 0; i<(total_rooms*3); i++) {  // If 2 rooms, need slots i 0,3.  (Because 1,2,4,5 are feedback slots). Room Label index are 0 and 1
                            if (i == 0) {
                                Room_Label_Index = 0;
                                label_value = Room_Label[Room_Label_Index];
                            }
                            else if(i%3==0) {
                                Room_Label_Index = i / 3;
                                label_value = Room_Label[Room_Label_Index];
                            } else {
                                label_value = "No Feedback";
                            }
                            CrestronConsole.PrintLine("i and labelel value " + i + label_value);
                            // roomName Room Control
                            TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{i + 1}"].StringValue = label_value;
                            TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].BooleanInput[$"Item {i + 1} Visible"].BoolValue = true;
                        }
                        // roomName Paging labels
                        for (int i = 0; i < (total_rooms); i += 1) {
                            TP1.SmartObjects[(uint)SmartObjectID.roomName].StringInput[$"text-o{i + 1}"].StringValue = Room_Label[i];
                            TP1.SmartObjects[(uint)SmartObjectID.roomName].BooleanInput[$"Item {i + 1} Visible"].BoolValue = true;
                        }
                    } else {
                        CrestronConsole.PrintLine("TP1 has gone Offline");
                    }
                }
            } catch (Exception e) {
                ErrorLog.Error("Error Loading Touchpanel: {0}", e.Message);
            }
        }

        void SmartObject_SigChange(GenericBase currentDevice, SmartObjectEventArgs args) {
            CrestronConsole.PrintLine("Sig Change Event Smart Object {0}", args.SmartObjectArgs.ID);
            CrestronConsole.PrintLine("Smart Object Details: signal {0}, number {1}, name {2}, bool {3}", args.Sig.GetType(), args.Sig.Number, args.Sig.Name, args.Sig.BoolValue);
            switch ((SmartObjectID)args.SmartObjectArgs.ID) {
                // Subpage selection
                case SmartObjectID.pageSelect:
                    if (args.SmartObjectArgs.BooleanOutput["Item 1 Pressed"].BoolValue) {
                        // Paging Page
                        TP1_Subpage.BoolValue = false;
                        TP1_Subpage = TP1.BooleanInput[20];
                        TP1_Subpage.BoolValue = true;
                    } else if (args.SmartObjectArgs.BooleanOutput["Item 3 Pressed"].BoolValue) {
                        // Camera Control Page
                        TP1_Subpage.BoolValue = false; // Turns off the current subpage
                        TP1_Subpage = TP1.BooleanInput[22]; // Sets the this new subpage
                        TP1_Subpage.BoolValue = true;
                    } else if (args.SmartObjectArgs.BooleanOutput["Item 2 Pressed"].BoolValue) {
                        // Room Recording Page
                        TP1_Subpage.BoolValue = false;
                        TP1_Subpage = TP1.BooleanInput[25];
                        TP1_Subpage.BoolValue = true;
                    } else if (args.SmartObjectArgs.BooleanOutput["Item 4 Pressed"].BoolValue) {
                        // Camera Control Room Page
                        TP1_Subpage.BoolValue = false;
                        TP1_Subpage = TP1.BooleanInput[8];
                        TP1_Subpage.BoolValue = true;
                    }
                    break;
                // Camera
                case SmartObjectID.camSelect:
                    if (args.Sig.Number > 1) {
                        int temp = (int)args.Sig.Number - 4011;
                        if (args.SmartObjectArgs.BooleanOutput[$"press{temp + 1}"].BoolValue) {
                            TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{temp + 1}"].BoolValue = true;
                            if (TP1_Camera != temp) {
                                TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{TP1_Camera + 1}"].BoolValue = false;
                            }
                            TP1_Camera = temp;
                        }
                    }
                    break;
                case SmartObjectID.camDPad:
                    if (args.SmartObjectArgs.BooleanOutput["Left"].BoolValue) {
                        // Camera Left PTZ
                        Camera[TP1_Camera].Pan(-5);
                    } else if (args.Sig.Name == "Left" && !args.SmartObjectArgs.BooleanOutput["Left"].BoolValue) {
                        // Camera Left PTZ Stop
                        Camera[TP1_Camera].Pan(0);
                    } else if (args.SmartObjectArgs.BooleanOutput["Up"].BoolValue) {
                        // Camera Up PTZ
                        Camera[TP1_Camera].Tilt(5);
                    } else if (args.Sig.Name == "Up" && !args.SmartObjectArgs.BooleanOutput["Up"].BoolValue) {
                        // Camera Up PTZ Stop
                        Camera[TP1_Camera].Tilt(0);
                    } else if (args.SmartObjectArgs.BooleanOutput["Right"].BoolValue) {
                        // Camera Left PTZ
                        Camera[TP1_Camera].Pan(5);
                    } else if (args.Sig.Name == "Right" && !args.SmartObjectArgs.BooleanOutput["Right"].BoolValue) {
                        // Camera Left PTZ Stop
                        Camera[TP1_Camera].Pan(0);
                    } else if (args.SmartObjectArgs.BooleanOutput["Down"].BoolValue) {
                        // Camera Up PTZ
                        Camera[TP1_Camera].Tilt(-5);
                    } else if (args.Sig.Name == "Down" && !args.SmartObjectArgs.BooleanOutput["Down"].BoolValue) {
                        // Camera Up PTZ Stop
                        Camera[TP1_Camera].Tilt(0);
                    }
                    break;
                case SmartObjectID.camZoom:
                    if (args.SmartObjectArgs.BooleanOutput["Tab Button 1 Press"].BoolValue) {
                        // Camera Zoom In
                        Camera[TP1_Camera].Zoom(5);
                    } else if (args.Sig.Name == "Tab Button 1 Press" && !args.SmartObjectArgs.BooleanOutput["Tab Button 1 Press"].BoolValue) {
                        // Camera Zoom Stop
                        Camera[TP1_Camera].Zoom(0);
                    } else if (args.SmartObjectArgs.BooleanOutput["Tab Button 2 Press"].BoolValue) {
                        // Camera Zoom Out
                        Camera[TP1_Camera].Zoom(-5);
                    } else if (args.Sig.Name == "Tab Button 2 Press" && !args.SmartObjectArgs.BooleanOutput["Tab Button 2 Press"].BoolValue) {
                        // Camera Zoom Stop
                        Camera[TP1_Camera].Zoom(0);
                    }
                    break;
                case SmartObjectID.camPreset:
                    if (args.SmartObjectArgs.BooleanOutput["Tab Button 1 Press"].BoolValue) {
                        // Camera Press Preset 1
                        Camera[TP1_Camera].Preset_Press(1);
                    } else if (args.Sig.Name == "Tab Button 1 Press" && !args.SmartObjectArgs.BooleanOutput["Tab Button 1 Press"].BoolValue) {
                        // Camera Release Preset 1
                        Camera[TP1_Camera].Preset_Release(1);
                    } else if (args.SmartObjectArgs.BooleanOutput["Tab Button 2 Press"].BoolValue) {
                        // Camera Press Preset 1
                        Camera[TP1_Camera].Preset_Press(2);
                    } else if (args.Sig.Name == "Tab Button 2 Press" && !args.SmartObjectArgs.BooleanOutput["Tab Button 2 Press"].BoolValue) {
                        // Camera Release Preset 2
                        Camera[TP1_Camera].Preset_Release(2);
                    } else if (args.SmartObjectArgs.BooleanOutput["Tab Button 3 Press"].BoolValue) {
                        // Camera Press Preset 3
                        Camera[TP1_Camera].Preset_Press(3);
                    } else if (args.Sig.Name == "Tab Button 3 Press" && !args.SmartObjectArgs.BooleanOutput["Tab Button 3 Press"].BoolValue) {
                        // Camera Release Preset 3
                        Camera[TP1_Camera].Preset_Release(3);
                    } else if (args.SmartObjectArgs.BooleanOutput["Tab Button 4 Press"].BoolValue) {
                        // Camera Press Preset 4
                        Camera[TP1_Camera].Preset_Press(4);
                    } else if (args.Sig.Name == "Tab Button 4 Press" && !args.SmartObjectArgs.BooleanOutput["Tab Button 4 Press"].BoolValue) {
                        // Camera Release Preset 4
                        Camera[TP1_Camera].Preset_Release(4);
                    }
                    break;
                case SmartObjectID.roomNameCameraSel:
                    break;
                // Room Recording Status Labels
                case SmartObjectID.roomNameRecordingSel:
                    if (args.Sig.Number > 1) { // rising edge check
                        int room_index2 = (int)args.Sig.Number - 4011; // room1 is 4011, room2 is 4014.....
                        // room_index2 = 0,2...

                        // Check if button was pressed (on rising edge)
                        if (args.SmartObjectArgs.BooleanOutput[$"press{room_index2 + 1}"].BoolValue) {
                            // If the button is off, light it up
                            if (TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{room_index2 + 1}"].BoolValue == false) {
                                TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{room_index2 + 1}"].BoolValue = true;
                            } else {
                                // If the button is on, turn it off
                                TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{room_index2 + 1}"].BoolValue = false;
                            }
                        }
                    }
                    break;
                // Paging Room
                case SmartObjectID.roomName:
                    if (args.Sig.Number > 1) { // rising edge check
                        int room_index = (int)args.Sig.Number - 4011; // room1 is 4011, room2 is 4012
                                                                      // Set the Labels.
                        if (args.SmartObjectArgs.BooleanOutput[$"press{room_index + 1}"].BoolValue) { // On Rising Edge 
                                                                                                      //If button off
                            if (TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{room_index + 1}"].BoolValue == false) {
                                // Light up button
                                TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{room_index + 1}"].BoolValue = true;
                                // Make route
                                // CREATE DICTIONARY: {6:TP1(page mic 1), 7:TP2(page mic 2)}. Pull Input 6 from it
                                // HARD CODED 6.
                                // Route
                                Update_Biamp(6, room_index + 6, true, false);
                            } else { // If button on
                                     // Turn off button
                                TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{room_index + 1}"].BoolValue = false;
                                // Unroute
                                Update_Biamp(6, room_index + 6, false, false);
                            }
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        void Touchpanel_SigChange(BasicTriList currentDevice, SigEventArgs args) {
            CrestronConsole.PrintLine("");
            CrestronConsole.PrintLine("Touchpanel_SigChange: signal {0}, number {1}, name {2}, bool {3}", args.Sig.GetType(), args.Sig.Number, args.Sig.Name, args.Sig.BoolValue);
            CrestronConsole.PrintLine("");
            if (currentDevice == TP1) {
                switch (args.Sig.Type) {
                    case eSigType.NA:
                        break;
                    case eSigType.Bool:
                        if (args.Sig.Number == 9 && args.Sig.BoolValue) {
                            TP1.BooleanInput[11].BoolValue = true; // Loads Main Subpage
                            TP1.BooleanInput[11].BoolValue = false; // Maybe since we turn it off here these lines aren't needed?
                            TP1_Subpage = TP1.BooleanInput[20]; // Loads Paging Subpage
                            TP1_Subpage.BoolValue = true;
                        }
                        // If we are on the rising edge of button, since don't want to do it twice.
                        if (args.Sig.BoolValue) { 
                            // Select or Deselect All rooms
                            if (args.Sig.Number == 408) {selectOrDeselectAllRooms(true);}
                            else if (args.Sig.Number == 409) {selectOrDeselectAllRooms(false);}
                            // Recording Actions
                            else if (args.Sig.Number == 410) { // Start Recording
                                LoopThroughRoomsAndExecuteAction(args, StartRecording);
                            } else if (args.Sig.Number == 411) { // Pause Recording
                                LoopThroughRoomsAndExecuteAction(args, PauseRecording);
                            } else if (args.Sig.Number == 412) { // Stop Recording
                                LoopThroughRoomsAndExecuteAction(args, StopRecording);
                            } else if (args.Sig.Number == 413) { // Enter Privacy
                                LoopThroughRoomsAndExecuteAction(args, EnterPrivacy);
                            } else if (args.Sig.Number == 414) { // Leave Privacy
                                LoopThroughRoomsAndExecuteAction(args, LeavePrivacy);
                            } else if (args.Sig.Number == 415) { // Mute Biamp
                                LoopThroughRoomsAndExecuteAction(args, MuteBiamp);
                            } else if (args.Sig.Number == 416) { // Unmute Biamp
                                LoopThroughRoomsAndExecuteAction(args, UnmuteBiamp);
                            }
                        }
                        break;
                    case eSigType.UShort:
                        break;
                    case eSigType.String:
                        break;
                    default:
                        break;
                }
            }
        }
        void selectOrDeselectAllRooms(bool select) {
            for (int i =0; i < total_rooms; i++) {
                TP1.SmartObjects[68].BooleanInput[$"fb{i + 1}"].BoolValue = select;
            }
        }
        void LoopThroughRoomsAndExecuteAction(SigEventArgs args, Action<int> roomAction) {
            // Loop through all rooms and execute the provided action if the button for the room is lit up
            for (int i = 0; i < total_rooms; i++) {
                if (TP1.SmartObjects[68].BooleanInput[$"fb{i + 1}"].BoolValue == true) {
                    // Execute the action for the room
                    roomAction(i);
                }
            }
        }

        // Define methods for each action
        void StartRecording(int roomIndex) {Room[roomIndex].Start_Recording();}
        void PauseRecording(int roomIndex) {Room[roomIndex].Pause_Recording();}
        void StopRecording(int roomIndex) {Room[roomIndex].Stop_Recording();}
        void EnterPrivacy(int roomIndex) {Room[roomIndex].Request_Privacy();}
        void LeavePrivacy(int roomIndex) {
            Room[roomIndex].Leave_Privacy();
        }
        void MuteBiamp(int roomIndex) {
            Mute_Update(roomIndex + 1, true); // Doing +1 because index 0 is Room ID 1
            Room[roomIndex].Mute_Room();
        }
        void UnmuteBiamp(int roomIndex) {
            Mute_Update(roomIndex + 1, false); // Doing +1 because index 0 is Room ID 1
            Room[roomIndex].UnMute_Room();
        }
        /*
        void Set_ButtonHighlightState(uint buttonNumber) {
            // Turn off all the buttons first
            TP1.BooleanInput[410].BoolValue = false;
            TP1.BooleanInput[411].BoolValue = false;
            TP1.BooleanInput[412].BoolValue = false;
            TP1.BooleanInput[413].BoolValue = false;
            //TP1.BooleanInput[414].BoolValue = false;

            // Now highlight the selected button
            TP1.BooleanInput[buttonNumber].BoolValue = true;
        }
        */
        void Update_TouchPanel_FeedBack_Status(int roomIndex) {
            // roomIndex 1 --> 2
            // roomIndex 2 --> 5
            // roomIndex 3 --> 8
            EMS_Room room_object = Room[roomIndex-1];
            int smart_object_fb = (roomIndex * 3) - 1;
            // Example mapping state to feedback signals
            switch (room_object.Get_State()) {
                case 1: // Recording Started
                    TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{smart_object_fb}"].StringValue = "Recording in Progress";
                    break;
                case 3: // Recording Paused
                    TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{smart_object_fb}"].StringValue = "Recording is Paused";
                    break;
                case 2: // Recording Stopped
                    TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{smart_object_fb}"].StringValue = "Recording is Stopped";
                    break;
                case 5: // Privacy Entered
                    TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{smart_object_fb}"].StringValue = "Privacy Entered";
                    break;
                //default:
                    // No state, ensure everything is off
                    //Set_ButtonHighlightState(0);  // Reset (turn off all buttons)
                    //break;
            }
            TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].BooleanInput[$"Item {smart_object_fb} Visible"].BoolValue = true;
        }
        void Update_TouchPanel_FeedBack_Mute(int roomIndex) {
            // roomIndex 1 --> 3
            // roomIndex 2 --> 6
            EMS_Room room_object = Room[roomIndex - 1];
            int smart_object_fb = roomIndex * 3;
            // Example mapping state to feedback signals
            if (room_object.Get_Mute_State() == 1) {
                TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{smart_object_fb}"].StringValue = "Mute is On";
            } else {
                TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{smart_object_fb}"].StringValue = "Mute is Off";
            }
            TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].BooleanInput[$"Item {smart_object_fb} Visible"].BoolValue = true;
        }

        void Set_Room_States_Func(string roomID, string value, string type, Boolean all_rooms) {
            if (all_rooms) {
                for (int i = 0; i < total_rooms; i++) {
                    ProcessRoomCommand(Room[i], type, value);
                }
            } else {
                int room_index = Int32.Parse(roomID.Substring(2)) - 1;
                ProcessRoomCommand(Room[room_index], type, value);
            }
        }
        void ProcessRoomCommand(EMS_Room room, string type, string value) {
            int state;
            switch (type) {
                case "status":
                    state = Int32.Parse(value);
                    room.Set_State(state);
                    CrestronConsole.PrintLine($"\nFB from SW room command set: {room.Get_State()}");
                    break;
                case "privacy":
                    state = (value == "1") ? 5 : 2;
                    room.Set_State(state);
                    CrestronConsole.PrintLine($"\nFB from SW room command set: {room.Get_State()}");
                    break;
                case "mute":
                    state = Int32.Parse(value);
                    room.Set_Mute_State(state);
                    CrestronConsole.PrintLine($"\nFB from SW room command set: {room.Get_Mute_State()}");
                    break;
                default:
                    CrestronConsole.PrintLine("\nUnknown command type.");
                    break;
            }
        }


        /* WAIT NOT NEEDED??
        StringBuilder BuildAckCommand(string[] data_list, bool privacy) {
            string status_value;
            string room_number;
            string type = "";
            StringBuilder ack_command = new StringBuilder(); // Doesn't need to build new object each time

            ack_command.Append("{");

            // WAIT DON"T NEED THIS FOR LOOP
            //
            //

            if (int.Parse(data_list[0]) == 0) {
                // Loop through All Room Id's to build command. 
            } else {
                for (int i = 0; i < data_list.Length; i++) {
                    status_value = Room[int.Parse(data_list[i]) - 1].Get_State().ToString(); // NOTE: data_list[i]-1 because Room1 is index 0 of Room[] 
                    room_number = data_list[i];
                    // "Status" requests return 5 for privacy.  
                    // "Privacy" requests return 1 for privacy.
                    if (privacy == true) {type = "Privacy";}
                    if (privacy == false) {type = "Status";}
                    if (privacy == false && status_value == "4") {status_value = "5";}
                    if (privacy == true && status_value == "4") {status_value = "1";}
                    if (privacy == true && status_value != "4") { status_value = "0"; }
                    if (i <= 8) {room_number = "0" + room_number;}
                    ack_command.Append("[ACK]")
                               .Append("[")
                               .Append(type)
                               .Append("]")
                               .Append("[RM")
                               .Append(room_number)
                               .Append("]")
                               .Append("[")
                               .Append(status_value)
                               .Append("]");
                    if (i != (data_list.Length - 1)) {
                        ack_command.Append(",");
                    }
                }
            }
            ack_command.Append("}");
            return ack_command;
        }
        private void EmsRequestStatusPrivacy(List<string> data, bool privacy) {
            //data = ["1,2"]
            //data[0] = "1,2"
            // data_list = ["1","2"] or ["0"]
            string[] data_list = data[0].Split(','); // string[] array over a dynamic List<string>, efficient.

            // If all rooms, change data_list to be all rooms.
            if (int.Parse(data_list[0]) == 0) {
                data_list = new string[total_rooms];
                for (int i = 0; i < total_rooms; i++) {
                    data_list[i] = (i + 1).ToString();
                }
            }
            StringBuilder ack_command = BuildAckCommand(data_list, privacy);
            string ack_command_result = ack_command.ToString();
            EMS_Modules.TCP_Server_Send(ack_command_result);
        }
        */

        StringBuilder BuildMuteAck(string[] data_list) {
            string status_value;
            string room_number; // "03"
            StringBuilder ack_command = new StringBuilder(); // Doesn't need to build new object each concatenation
            ack_command.Append("{");
            for (int i = 0; i < data_list.Length; i++) {
                status_value = Room[int.Parse(data_list[i]) - 1].Get_Mute_State().ToString(); // NOTE: data_list[i]-1 because Room1 is index 0 of Room[] 
                room_number = data_list[i];
                ack_command.Append("[ACK][Mute]")
                            .Append("[RM")
                            .Append(room_number)
                            .Append("]")
                            .Append("[")
                            .Append(status_value)
                            .Append("]");
                if (i != (data_list.Length - 1)) {ack_command.Append(",");}
            }
            ack_command.Append("}");
            return ack_command;
        }

        private void EmsRequestMute(List<string> data) {
            //data = ["RM01,RM02"]
            //data[0] = "RM01,RM02"
            //data_list = ["RM01","RM02"] or ["0"]
            string[] data_list = data[0].Split(','); // string[] array over a dynamic List<string>, efficient.
            // If all rooms, change data_list to be all rooms.
            if (data_list[0] == "0") {
                data_list = new string[total_rooms];
                for (int i = 0; i < total_rooms; i++) {data_list[i] = (i + 1).ToString();}
            } else {
                for (int i = 0; i<data_list.Length; i++) { data_list[i] = data_list[i].Substring(2);} // Remove "RM" prefix
            }
            StringBuilder ack_command = BuildMuteAck(data_list);
            string ack_command_result = ack_command.ToString();
            EMS_Modules.TCP_Server_Send(ack_command_result);
        }
        void Cam_Preset_Saved(string data) {
            Pulse_Digital(TP1.BooleanInput[74], 500);
        }
        void Pulse_Digital(BoolInputSig signal, int time) {
            Thread temp = new Thread(() => Pulse_Digital_Thread(signal, time));
            temp.Start();
        }
        void Pulse_Digital_Thread(BoolInputSig signal, int time) {
            signal.BoolValue = true;
            Thread.Sleep(time);
            signal.BoolValue = false;
        }
        void ComPort_Receive_Data(ComPort ReceivingComPort, ComPortSerialDataEventArgs args) {
            string temp = args.SerialData;
            for (uint i = 0; i < this.ComPorts.Count; i++) {
                if (ReceivingComPort.Equals(this.ComPorts[i])) {
                    if (temp.Contains("PLY")) {
                        //
                    }
                    if (temp.Contains("STP")) {
                        //
                    }
                }
            }
        }

        void Debug_Print(string data) {
            if (emsDebugState) CrestronConsole.PrintLine($"Debug: {data}");
        }

        void TCP_Client_Receive_Data(string data) {
            if (emsDebugState) CrestronConsole.PrintLine("Received Data: {0}", data);
            EMS_Modules.EMS_Parse(data);
        }

        void TCP_Server_Receive_Data(string data) {
            if (emsDebugState) CrestronConsole.PrintLine("Received Data: {0}", data);
            EMS_Modules.EMS_Parse(data);
        }

        void Play_Message(uint player, uint message) {
            if (player > 0 && this.ComPorts.Count >= player) {
                this.ComPorts[player].Send(String.Format("PLY{0}\n", message));
                if (emsDebugState) CrestronConsole.PrintLine("Playing Message {0} on Player {1}", message, player);
                EMS_Modules.TCP_Server_Send($"{{[ACK][Play][{player}][{message}]}}\n");
            }
        }

        void Record_Message(uint player, uint message) {
            if (player > 0 && this.ComPorts.Count >= player) {
                this.ComPorts[player].Send(String.Format("REC{0}\n", message));
                if (emsDebugState) CrestronConsole.PrintLine("Recording Message {0} on Player {1}", message, player);
                EMS_Modules.TCP_Server_Send($"{{[ACK][Record][{player}][{message}]}}\n");
            }
        }

        void Stop_Message(uint player, uint message) {
            if (player > 0 && this.ComPorts.Count >= player) {
                this.ComPorts[player].Send(String.Format("STP{0}\n", message));
                if (emsDebugState) CrestronConsole.PrintLine("Stopping Message {0} on Player {1}", message, player);
                EMS_Modules.TCP_Server_Send($"{{[ACK][Stop][{player}]}}\n");
            }
        }

        void Request_Ping_Func() {
            if (emsDebugState) CrestronConsole.PrintLine("Ping Received");
            EMS_Modules.TCP_Server_Send("{[ACK][Ping]}\n");
        }

        // Biamp Functions
        private void ReceiveData(string returnedData) {
            CrestronConsole.PrintLine("\n\n\nReceiveData returned:\n\n\n");
            CrestronConsole.PrintLine(returnedData);
        }
        private void ObjectFeedback(AbstractBiampObject obj, string value) {
            CrestronConsole.PrintLine("\n\nObjectFeedback data returned: ");
            CrestronConsole.PrintLine(value);
            CrestronConsole.PrintLine(obj.PrintLine());
            CrestronConsole.PrintLine("\n\n");
        }

        void Biamp_Processing(int input, int output, bool route) {
            string instance = "Router1";
            // Biamp Crosspoint (Mixer Matrix) object setup 
            if (crosspointMap.ContainsKey((input, output))) {
                biampCrosspoint = crosspointMap[(input, output)];
            } else {
                biampCrosspoint = new BiampCrosspoint(biamp, instance, input, output);
                // When driver receives line from the biamp, it loops through this list to match it to the right object.
                biamp.Subscribe(biampCrosspoint);
                // Add pair to muteMap
                crosspointMap[(input, output)] = biampCrosspoint;
            }
            if (route == true) {
                biampCrosspoint.SetOn();
                if (emsDebugState) CrestronConsole.PrintLine("Routing Biamp input {0} to output {1}", input, output);
                if (biamp.isConnected()) EMS_Modules.TCP_Server_Send($"{{[ACK][Route][{input}][{output}]}}\n");

            } else {
                biampCrosspoint.SetOff();
                if (emsDebugState) CrestronConsole.PrintLine("Unrouting Biamp input {0} to output {1}", input, output);
                if (biamp.isConnected()) EMS_Modules.TCP_Server_Send($"{{[ACK][Clear][{input}][{output}]}}\n");
            }
        }
     
        void Update_Biamp(int input, int output, bool route, bool all_rooms) {
            if (all_rooms == true && route == true) {
                // Route to all rooms
                for (int i = 1; i <= total_rooms; i++) {
                    Biamp_Processing(input, (i+5), route);
                }
            } else {
                Biamp_Processing(input, output, route);
            }
        }
        void Preset_Biamp(string name) {
            biamp.RecallPresetByName(name);
            if (emsDebugState) CrestronConsole.PrintLine("Setting Biamp Preset {0}", name);
            string[] parts = name.Split('_');

            int input = Int32.Parse(parts[1]);

            if (parts[2] == "Clear") {
                if (biamp.isConnected()) EMS_Modules.TCP_Server_Send($"{{[ACK][Clear][{input}][0]}}\n");
            } else { // Zoning
                if (biamp.isConnected()) EMS_Modules.TCP_Server_Send($"{{[ACK][Zone][{input}][{parts[2]}]}}\n");
            }

        }

        void Mute_Processing(int room_index, bool mute) {
            string instance = "Mute1";
            // Biamp Mute object setup 
            if (muteMap.ContainsKey(room_index)) {
                biampMute = muteMap[room_index];
            } else {
                biampMute = new BiampMute(biamp, instance, room_index);
                // When driver receives line from the biamp, it loops through this list to match it to the right object.
                biamp.Subscribe(biampMute);
                // If an external device changes the biamp, we want to get the update on that.
                biampMute.Subscribe();
                // Add pair to muteMap
                muteMap[room_index] = biampMute;
            }
            // Send data to Biamp
            if (mute) {
                biampMute.SetMuteOn();
                if (emsDebugState) CrestronConsole.PrintLine("Muting Biamp: Instance {0} Channel {1}", instance, room_index);
                if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][RM0{room_index}][1]}}\n");
            } else {
                biampMute.SetMuteOff();
                if (emsDebugState) CrestronConsole.PrintLine("UnMuting Biamp: Instance {0} Channel {1}", instance, room_index);
                if (biamp.isConnected() == true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][RM0{room_index}][0]}}\n");
            }
        }

        void Mute_Update(int room_index, bool mute) {
            if (room_index == 0) {
                for (int i = 1; i <= total_rooms; i++) {
                    Mute_Processing(i, mute);
                }
            } else {
                Mute_Processing(room_index, mute);
            }
        }

        /// <summary>
        /// Event Handler for Ethernet events: Link Up and Link Down. 
        /// Use these events to close / re-open sockets, etc. 
        /// </summary>
        /// <param name="ethernetEventArgs">This parameter holds the values 
        /// such as whether it's a Link Up or Link Down event. It will also indicate 
        /// which Ethernet adapter this event belongs to.
        /// </param>
        void _ControllerEthernetEventHandler(EthernetEventArgs ethernetEventArgs) {
            switch (ethernetEventArgs.EthernetEventType) {//Determine the event type Link Up or Link Down
                case (eEthernetEventType.LinkDown):
                    //Next need to determine which adapter the event is for. 
                    //LAN is the adapter is the port connected to external networks.
                    if (ethernetEventArgs.EthernetAdapter == EthernetAdapterType.EthernetLANAdapter) {
                        EMS_Modules.TCP_Server_Stop();
                        biamp.Disconnect();
                    }
                    break;
                case (eEthernetEventType.LinkUp):
                    if (ethernetEventArgs.EthernetAdapter == EthernetAdapterType.EthernetLANAdapter) {
                        EMS_Modules.TCP_Server_Start("0.0.0.0", 53124);
                        biamp.ConnectAndStartThreads();
                    }
                    break;
            }
        }

        /// <summary>
        /// Event Handler for Programmatic events: Stop, Pause, Resume.
        /// Use this event to clean up when a program is stopping, pausing, and resuming.
        /// This event only applies to this SIMPL#Pro program, it doesn't receive events
        /// for other programs stopping
        /// </summary>
        /// <param name="programStatusEventType"></param>
        void _ControllerProgramEventHandler(eProgramStatusEventType programStatusEventType) {
            switch (programStatusEventType) {
                case (eProgramStatusEventType.Paused):
                    //The program has been paused.  Pause all user threads/timers as needed.
                    break;
                case (eProgramStatusEventType.Resumed):
                    //The program has been resumed. Resume all the user threads/timers as needed.
                    break;
                case (eProgramStatusEventType.Stopping):
                    //The program has been stopped.
                    //Close all threads. 
                    //Shutdown all Client/Servers in the system.
                    //General cleanup.
                    //Unsubscribe to all System Monitor events

                    biamp.Disconnect();
                    ErrorLog.Notice("Biamp Disconnected");
                    break;
            }
        }

        /// <summary>
        /// Event Handler for system events, Disk Inserted/Ejected, and Reboot
        /// Use this event to clean up when someone types in reboot, or when your SD /USB
        /// removable media is ejected / re-inserted.
        /// </summary>
        /// <param name="systemEventType"></param>
        void _ControllerSystemEventHandler(eSystemEventType systemEventType) {
            switch (systemEventType) {
                case (eSystemEventType.DiskInserted):
                    //Removable media was detected on the system
                    break;
                case (eSystemEventType.DiskRemoved):
                    //Removable media was detached from the system
                    break;
                case (eSystemEventType.Rebooting):
                    //The system is rebooting. 
                    //Very limited time to preform clean up and save any settings to disk.

                    biamp.Disconnect();
                    ErrorLog.Notice("Biamp Disconnected");
                    break;
            }
        }


    }
}