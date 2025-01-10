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
        int TP1_Camera, TP1_Room = 0;
        Dictionary<int, BiampMute> muteMap = new Dictionary<int, BiampMute>();

        // Biamp objects
        BiampMute biampMute;

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
                EMS_Modules.Biamp_Route += new biampRouteHandler(Route_Biamp);
                EMS_Modules.Biamp_UnRoute += new biampRouteHandler(UnRoute_Biamp);
                EMS_Modules.Biamp_Preset += new biampPresetHandler(Preset_Biamp);
                EMS_Modules.Biamp_Mute_Update += new biampMuteHandler(Mute_Update);
                
                EMS_Modules.TCP_Server_Receive_Data += new tcpDataHandler(TCP_Server_Receive_Data);
                EMS_Modules.EMS_Client_Receive_Data += new tcpDataHandler(TCP_Client_Receive_Data);
                EMS_Modules.Debug_Print += new New_John_EMS_Library.debugHandler(Debug_Print);
                EMS_Camera.TCP_Client_Send += new tcpDataSend(TCP_Client_Send);
                EMS_Camera.Preset_Saved += new cameraControl(Cam_Preset_Saved);
                EMS_Room.TCP_Client_Send += new tcpDataSend(TCP_Client_Send);
                EMS_Modules.EmsToCrestronStatus += new emsToCrestronStatusRequest(EmsToCrestronStatus);
                EMS_Modules.Set_Room_States_Event += new set_Room_States_Handler(Set_Room_States_Func);

                // Biamp Library Events
                BiampMute.Debug_Print += new debugHandler2(Debug_Print);

                //Create custom console commands to control the processor
                CrestronConsole.AddNewConsoleCommand(EMSDebug, "emsdebug", "on or off to debug EMS signals", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(TCP_Client_Send, "csend", "send a command to the tcp client", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(TCP_Server_Send, "ssend", "send a command to the tcp server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(TCP_Server_Stop, "sstop", "close our tcp server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(EMS_Load_Config, "loadConfig", "pull config data from csv file", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(EMS_Change_IP, "emsIP", "Set new ip address for the ems server", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(Biamp_Change_IP, "biampIP", "Set new ip address for the biamp processor ssh connection", ConsoleAccessLevelEnum.AccessAdministrator);
                CrestronConsole.AddNewConsoleCommand(BiampClientDisconnect, "discBC", "disconnect our Biamp Library client from the Biamp server", ConsoleAccessLevelEnum.AccessAdministrator);

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
                        }  else if (line.Contains("Server_Port")) {
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
                        //Room Labels
                        TP1_Room = -1;
                        for (int i = 0; i < 2; i++) {  // HARD CODE 2 rooms
                            // roomName Paging 
                            TP1.SmartObjects[(uint)SmartObjectID.roomName].StringInput[$"text-o{i + 1}"].StringValue = Room_Label[i];
                            TP1.SmartObjects[(uint)SmartObjectID.roomName].BooleanInput[$"Item {i + 1} Visible"].BoolValue = true;

                            // roomName Room Control
                            TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].StringInput[$"text-o{i + 1}"].StringValue = Room_Label[i];
                            TP1.SmartObjects[(uint)SmartObjectID.roomNameRecordingSel].BooleanInput[$"Item {i + 1} Visible"].BoolValue = true;
                            if (TP1_Room == -1) { // Just highlights Room 1 Select at the start.
                                TP1_Room = i;
                                TP1.SmartObjects[(uint)SmartObjectID.camSelect].BooleanInput[$"fb{TP1_Camera + 1}"].BoolValue = true;
                            }
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
                // Room Recording Status
                case SmartObjectID.roomNameRecordingSel:
                    if (args.Sig.Number > 1) {
                        int temp = (int)args.Sig.Number - 4011; // room1 is 4011, room2 is 4012
                        if (args.SmartObjectArgs.BooleanOutput[$"press{temp + 1}"].BoolValue) {
                            TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{temp + 1}"].BoolValue = true;
                            if (TP1_Room != temp) {
                                TP1.SmartObjects[args.SmartObjectArgs.ID].BooleanInput[$"fb{TP1_Room + 1}"].BoolValue = false;
                            }
                            TP1_Room = temp;
                        }
                    }
                    break;
                // Paging
                case SmartObjectID.roomName:
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
                        if (args.Sig.BoolValue) { // If we are on the rising edge of button, since don't want to do it twice.
                            // Room Control Subpage Buttons
                            if (args.Sig.Number == 410) {
                                // Start Recording
                                Room[TP1_Room].Start_Recording();
                                Room[TP1_Room].Set_State(1);
                                /*
                                 * 
                                 * When we want to highlight the buttons, do 
                                 * TP1.BooleanInput[410].BoolValue = true;
                                 * 
                                */
                            } else if (args.Sig.Number == 411) {
                                // Pause Recording
                                Room[TP1_Room].Pause_Recording();
                                Room[TP1_Room].Set_State(3);
                                
                            } else if (args.Sig.Number == 412) {
                                // Stop recording
                                Room[TP1_Room].Stop_Recording();
                                Room[TP1_Room].Set_State(2);
                            } else if (args.Sig.Number == 413) {
                                // Enter privacy
                                Room[TP1_Room].Request_Privacy();
                                Room[TP1_Room].Set_State(4);
                            } else if (args.Sig.Number == 414) {
                                // Leave Privacy
                                Room[TP1_Room].Leave_Privacy();
                                Room[TP1_Room].Set_State(2);
                            } else if (args.Sig.Number == 415) {
                                // Mute Biamp
                                Mute_Update(TP1_Room + 1, true); // Doing + 1, Because index 0 is Room ID 1 
                                // Tell EMS
                                Room[TP1_Room].Mute_Room();
                                Room[TP1_Room].Set_Mute_State(1);
                            } else if (args.Sig.Number == 416) {
                                // Unmute Biamp
                                Mute_Update(TP1_Room + 1, false);
                                // Tell EMS
                                Room[TP1_Room].UnMute_Room();
                                Room[TP1_Room].Set_Mute_State(0);
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

        void Set_Room_States_Func(string roomID, string value, string type, Boolean all_rooms) {
            if (all_rooms) {
                for (int i = 0; i < total_rooms; i++) {
                    ProcessRoomCommand(Room[i], type, value);
                }
            } else {
                int room_index = Int32.Parse(roomID.Substring(2))-1;
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
                    state = (value == "1") ? 4 : 2;
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

        /*
        if (all_rooms == true) {
            if (type == "status") { // status
                for (int i = 0; i < total_rooms; i++) {
                    Room[i].Set_State(Int32.Parse(value));
                    CrestronConsole.PrintLine("\nFB from SW room command set :" + Room[i].Get_State().ToString());
                }
            } else if (type == "privacy") { // privacy
                if (value == "1"){
                    value = "4";
                } else {
                    value = "2";
                }
                for (int i = 0; i < total_rooms; i++) {
                    Room[i].Set_State(Int32.Parse(value));
                    CrestronConsole.PrintLine("\nFB from SW room command set :" + Room[i].Get_State().ToString());
                }
            } else {
                for (int i = 0; i < total_rooms; i++) {
                    Room[i].Set_Mute_State(Int32.Parse(value));
                    CrestronConsole.PrintLine("\nFB from SW room command set :" + Room[i].Get_Mute_State().ToString());
                }
            }
        } 
        else {
            int room_index = Int32.Parse(roomID.Substring(2))-1;
            if (type == "status") {
                Room[room_index].Set_State(Int32.Parse(value));
                CrestronConsole.PrintLine("\nFB from SW room command set :" + Room[room_index].Get_State().ToString());
            } else if (type == "privacy") {
                if (value == "1") {
                    Room[room_index].Set_State(4);
                } else {
                    Room[room_index].Set_State(2);
                }
                CrestronConsole.PrintLine("\nFB from SW room command set :" + Room[room_index].Get_State().ToString());
            } else if (type == "mute") {
                Room[room_index].Set_Mute_State(Int32.Parse(value));
                CrestronConsole.PrintLine("\nFB from SW room command set :" + Room[room_index].Get_Mute_State().ToString());
            }
        }
        */

        private void EmsToCrestronStatus(List<string> data) {
            //data = ["1,2"]
            //data[0] = "1,2"
            // data_list = ["1","2"] or ["0"]
            string[] data_list = data[0].Split(','); // string[] array over a dynamic List<string>, efficient.
            string status_value;
            string room_number;
            StringBuilder ack_command = new StringBuilder(); // Doesn't need to build new object each time
            string ack_command_result;
            ack_command.Append("{");

            /*
                NOTE: data_list[i]-1 because Room1 is index 0 of Room[] 
            */

            if (int.Parse(data_list[0]) == 0) {
                // Loop through All Room Id's to build command. 
            } else {
                for (int i = 0; i < data_list.Length; i++) {
                    status_value = Room[int.Parse(data_list[i])-1].Get_State().ToString();
                    room_number = data_list[i];
                    ack_command.Append("[ACK][Status]")
                               .Append("[")
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
            ack_command_result = ack_command.ToString();
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

        void Route_Biamp(int input, int output) {
            string command = string.Format("{0} set crosspointLevelState {1} {2} true", "Router1", input, output);
            biamp.SendData(command);
            if (emsDebugState) CrestronConsole.PrintLine("Routing Biamp input {0} to output {1}", input, output);
            if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Route][{input}][{output}]}}\n");
        }

        void UnRoute_Biamp(int input, int output) {
            string command = string.Format("{0} set crosspointLevelState {1} {2} false", "Router1", input, output);
            biamp.SendData(command);
            if (emsDebugState) CrestronConsole.PrintLine("Unrouting Biamp input {0} to output {1}", input, output);
            if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Clear][{input}][{output}]}}\n");
        }

        void Preset_Biamp(string name) {
            biamp.RecallPresetByName(name);
            if (emsDebugState) CrestronConsole.PrintLine("Setting Biamp Preset {0}", name);

            ErrorLog.Notice("PRESET_BIAMP FUNC...Biamp Object is {0}. Biamp IP is {1}", biamp, biamp_IP[0]);

            //if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send("{[ACK][Clear][][0]}\n");
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

        /*
        void Mute_Biamp(string name, bool from_crestron) {


            if (muteMap.ContainsKey()) {

            }

            // Put this at class level
            //Dictionary<string, BiampMute> muteMap = new Dictionary<string, BiampMute>();
            // for mute and unmute.  can do the same check. if object already exists. WAIT THAT WONT HELP BECAUSE 
            // THE driver list won't change 
           

            //biampMute = new BiampMute(biamp, "Mute1", 1);
            // need below line because when driver receives line from the biamp, want to match it to the right object.
            //biamp.Subscribe(biampMute);
            // below subscribe because we if an external device changes the biamp, we want to get the update on that.
            //biampMute.Subscribe();
            //biampMute.SetMuteOff();
            //biampMute.Toggle();

            // name = "1" for "RM01"
            if (from_crestron) {
                string command = string.Format("Mute_{0} set mute 1 true", name);
                biamp.SendData(command);
                if (emsDebugState) CrestronConsole.PrintLine("Muting Biamp mute: Mute1", );
                if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][{i + 1}][1]}}\n");
            }
            // from_SW
            else if (name == "0") {    
                for (int i = 0; i < total_rooms; i++) {
                    string command = string.Format("Mute_{0} set mute 1 true", i+1);
                    biamp.SendData(command);
                    if (emsDebugState) CrestronConsole.PrintLine("Muting Biamp mute: Mute_{0}", i+1);
                    if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][{i+1}][1]}}\n");
                }
            } else { // name = say "RM01"
                int room_index = Int32.Parse(name.Substring(2)) - 1;
                string command = string.Format("Mute_{0} set mute 1 true", room_index);
                biamp.SendData(command);
                if (emsDebugState) CrestronConsole.PrintLine("Muting Biamp mute: Mute_{0}", room_index);
                if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][{room_index}][1]}}\n");
            }
        }

        void UnMute_Biamp(string name) {
            if (name == "0") {
                for (int i = 0; i < total_rooms; i++) {
                    string command = string.Format("Mute_{0} set mute 1 false", i + 1);
                    biamp.SendData(command);
                    if (emsDebugState) CrestronConsole.PrintLine("UnMuting Biamp mute: Mute_{0}", i + 1);
                    if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][{i + 1}][0]}}\n");
                }
            } else { // name = say "RM01"
                int room_index = Int32.Parse(name.Substring(2))-1;
                string command = string.Format("Mute_{0} set mute 1 false", room_index);
                biamp.SendData(command);
                if (emsDebugState) CrestronConsole.PrintLine("UnMuting Biamp mute: Mute_{0}", room_index);
                if (biamp.isConnected() || true) EMS_Modules.TCP_Server_Send($"{{[ACK][Mute][{room_index}][0]}}\n");
            }
        }
        */

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