using System;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace New_John_EMS_Library {

    public delegate void messagePlayerHandler(uint player, uint message);
    public delegate void biampRouteHandler(int input, int output, bool route, bool all_rooms);
    public delegate void biampPresetHandler(string presetName);
    public delegate void biampMuteHandler(int roomId, bool mute_state);
    public delegate void requestHandler();
    public delegate void tcpDataHandler(string data);
    public delegate void tcpDataSend(string data);
    public delegate void debugHandler(string data);
    public delegate void cameraControl(string data);
    public delegate void emsToCrestronStatusRequest(List<string> data);
    public delegate void set_Room_States_Handler(string roomID, string value, string type, Boolean all_rooms);

    public static class EMS_Modules {

        static string DELIMITER = "";
        static string COMMAND_MATCH;
        static List<string> COMMAND_TYPE;
        static List<List<string>> COMMAND_FUNCTION_MAP;
        static List<string> CAMERA_FUNCTION;
        static List<string> ROOM_FUNCTION;
        static List<string> PAGING_FUNCTION;
        static List<string> MESSAGE_FUNCTION;
        static List<string> REQUEST_FUNCTION;
        static List<string> ACK_FUNCTION;

        static double DURATION;

        public static event emsToCrestronStatusRequest EmsToCrestronStatus;
        public static event set_Room_States_Handler Set_Room_States_Event;

        public static event messagePlayerHandler Message_Play;
        public static event messagePlayerHandler Message_Record;
        public static event messagePlayerHandler Message_Delete;
        public static event messagePlayerHandler Message_Stop;
        public static event biampRouteHandler Biamp_Update;
        public static event biampMuteHandler Biamp_Mute_Update;
        public static event biampPresetHandler Biamp_Preset;
        public static event requestHandler Request_Ping;
        public static event tcpDataHandler TCP_Server_Receive_Data;
        public static event tcpDataHandler EMS_Client_Receive_Data;
        public static event debugHandler Debug_Print;

        private static Thread TCP_Server_Thread = null;
        private static TcpListener TCP_Server = null;
        private static TcpClient[] TCP_Server_Clients = { null, null };
        private static Thread[] client_thread = { null, null };

        private static TcpClient EMS_Client;
        private static NetworkStream EMS_Client_Stream;
        private static Thread EMS_Client_Thread;

        private static Thread EMS_Timeout_Thread = null;
        private static DateTime EMS_Client_Start_Time;

        /// <summary>
        /// Function to initialize some reference variables for the rest of the library to use
        /// </summary>
        /// <returns>Nothing</returns>
        public static void Declare_Constants() {
            DELIMITER = "\n";

            //COMMAND_MATCH = $@"\{{(\[.*?\])+(\,(\[.*?\])+)*\}}{Regex.Escape(DELIMITER)}";

               //We may need to use the Command Match line below.  But that would mean changing a bit of EMS parse as well.
               //When I pass in command as string with \n. It works here
               //BUT, when using Putty, if I need to change the command Match to the one below.  And pass it in as just the string
               //Putty automatically creates a \r\n.  SO, think should test everything without Putty first.  Then come back to this with actual SW team.
               //MAYBE?  Cause TCP code is tied in so not sure yet.

            // Regex below handles \n macOS. AND \r\n common in Windows
            COMMAND_MATCH = $@"\{{(\[.*?\])+(\,(\[.*?\])+)*\}}(\r?\n)";

            COMMAND_TYPE = new List<string> { "Camera", "Room", "Paging", "Message", "Request", "ACK" };
            CAMERA_FUNCTION = new List<string> { "Pan", "Tilt", "Zoom", "Position", "Recall", "Save" };
            ROOM_FUNCTION = new List<string> { "Status", "Privacy", "Mute" };
            PAGING_FUNCTION = new List<string> { "Route", "Zone", "Clear" };
            MESSAGE_FUNCTION = new List<string> { "Play", "Record", "Delete", "Stop" };
            REQUEST_FUNCTION = new List<string> { "Status", "Privacy", "Mute", "Route", "Position", "Ping", "Header" };
            ACK_FUNCTION = new List<string> { "Pan", "Tilt", "Zoom", "Position", "Recall", "Save", "Status", "Privacy", "Mute", "Ping" };
            COMMAND_FUNCTION_MAP = new List<List<string>> { CAMERA_FUNCTION, ROOM_FUNCTION, PAGING_FUNCTION, MESSAGE_FUNCTION, REQUEST_FUNCTION, ACK_FUNCTION };

            DURATION = 8;
        }

        /// <summary>
        /// Function to parse and filter string commands from the EMS server
        /// For the purpose of paging routing, automated messaging and room status/control
        /// </summary>
        /// <param name="ems_rx">Command string to parse from server</param>
        /// <returns>Nothing</returns>
        public static void EMS_Parse(string ems_rx) {
            bool processed;

            Debug_Print("PARAM passed" + ems_rx);

            Match match = Regex.Match(ems_rx, COMMAND_MATCH);
            
            if (!match.Success) {
                // Incomplete Command
                Debug_Print($"Incomplete Command: [{ems_rx}]");
                TCP_Server_Send($"{{[ERR][Incomplete][{ems_rx}]}}\n");
            } else {
                ems_rx = match.Value;
                // ems_rx = {[Paging][Clear][2][0],[Paging][Clear][2][3,4],[Paging][Route][2][1,2,3,4,5],[Paging][Route][2][0]}

                // Replaces either "\n" or "\r\n"
                ems_rx = ems_rx.Replace("{", "").Replace("}", "").Replace("\n", "").Replace("\r\n", "") + ",[";

                Debug_Print($"Complete Command: [{ems_rx}]");
                List<string> commands = new List<string>();
                int index = ems_rx.IndexOf(",[");
                int index2 = -1;
                while (index > -1) {
                    commands.Add(ems_rx.Remove(index));
                    ems_rx = ems_rx.Substring(index + 1, ems_rx.Length - index - 1);
                    index = ems_rx.IndexOf(",[");
                }
 
                Debug_Print($"Command Totals: {commands.Count}");
                while (commands.Count > 0) {
                    List<string> command = new List<string>();
                    foreach (Match m in Regex.Matches(commands.First(), @"\[(.*?)\]")) {
                        command.Add(m.Groups[1].Value);
                    }
                    try {
                        index = COMMAND_TYPE.IndexOf(command[0]);
                        index2 = COMMAND_FUNCTION_MAP[index].IndexOf(command[1]);
                        if (index < 0 || index2 < 0) {
                            // Bad Command
                            throw new Exception("Incorrect Command Format");
                        }
                    } catch {
                        Debug_Print($"Bad Command: [{commands[0]}]");
                        TCP_Server_Send($"{{[ERR][Bad][{commands[0]}]}}\n");
                    }
                    // Process command here
                    Debug_Print($"Processing: {commands[0]}");
                    processed = false;
                    switch (index) {
                        case 0: // Camera Functions
                            processed = Camera_Command(index2, command.GetRange(2, command.Count - 2));
                            break;
                        case 1: // Room Functions
                            processed = Room_Command(index2, command.GetRange(2, command.Count - 2));
                            break;
                        case 2: // Paging Functions
                            processed = Paging_Command(index2, command.GetRange(2, command.Count - 2));
                            break;
                        case 3: // Message Player Functions
                            processed = Message_Command(index2, command.GetRange(2, command.Count - 2));
                            break;
                        case 4: // Request Functions
                            processed = Request_Command(index2, command.GetRange(2, command.Count - 2));
                            break;
                        case 5: // ACK Response
                            processed = ACK_Response(index2, command.GetRange(2, command.Count - 2));
                            break;
                        default:
                            // Unimplimented Command
                            Debug_Print($"Unknown Command: [{commands[0]}]");
                            TCP_Server_Send($"{{[ERR][Unknown][{commands[0]}]}}\n");
                            processed = true;
                            break;
                    }

                    if (!processed) {
                        // Bad Command
                        Debug_Print($"Bad Command: [{commands[0]}]");
                        TCP_Server_Send($"{{[ERR][Bad][{commands[0]}]}}\n");
                    }
                    commands.RemoveAt(0);
                }
            }

            /*
            temp = ems_rx.Substring(5,ems_rx.Length-5);
            if(temp[0] == '2'){
                // Paging Route Command
                int input = 0;
                int output = 0;
                Int32.TryParse(ems_rx.Substring(8, 2), out input);
                Int32.TryParse(ems_rx.Substring(10, 2), out output);
            }else if(temp[0] == '3'){
                // Message Player Command
                int player = 1;
                int message = 0;

                if(ems_rx.Length > 11){
                    Int32.TryParse(ems_rx.Substring(8, 2), out player);
                    Int32.TryParse(ems_rx.Substring(10, 2), out message);
                }else{
                    Int32.TryParse(ems_rx.Substring(8, 2), out message);
                }

                if(temp[2] == '1'){
                    // Play Message message
                    if(Message_Play != null){
                        Message_Play(player, message);
                    }
                }else if(temp[2] == '2'){
                    // Record Message message
                    if (Message_Record != null){
                        Message_Record(player, message);
                    }
                }
                else if(temp[2] == '3'){
                    // Stop Message message
                    if (Message_Stop != null){
                        Message_Stop(player, message);
                    }
                }
            }else{
                // Room Status Command
                if (ems_rx.Substring(0, 3) == "AUD"){
                    string roomKey = ems_rx.Substring(3, 4);

                    if (ems_rx[7] == '0'){
                        // Mute Room roomKey
                    }
                    else if (ems_rx[7] == '1'){
                        // Unmute Room roomKey
                    }
                }else if (ems_rx.Substring(0, 3) == "DVR"){
                    string roomKey = ems_rx.Substring(5, 4);

                    if (ems_rx.Substring(3, 2) == "SR"){
                        // Start Recording Room roomKey
                    }else if (ems_rx.Substring(3, 2) == "SR"){
                        // Stop Recording Room roomKey
                    }
                }else if (ems_rx.Substring(0, 3) == "ACK"){
                    string roomKey = ems_rx.Substring(3, 4);

                    if(ems_rx[7] == '1'){
                        // Room roomKey status recording
                    }else if(ems_rx[7] == '2'){
                        // Room roomkey status paused
                    }else if (ems_rx[7] == '3'){
                        // Room roomkey status stopped
                    }else if (ems_rx[7] == '4'){
                        // Room roomkey status error
                    }else if (ems_rx[7] == '5'){
                        // Room roomkey status privacy
                    }
                }
            }
            }
                 */
        }

        /// <summary>
        /// Function to execute camera commands from the EMS software
        /// </summary>
        /// <param name="command">Int command from Camera_Function to execute</param>
        /// <param name="data">String list of data needed to execute the command</param>
        /// <returns>Boolean as to whether the command was executed or not</returns>
        public static bool Camera_Command(int command, List<string> data) {
            switch (command) {
                case 0: // Pan
                    TCP_Server_Send($"{{[ACK][Pan][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                    return true;
                case 1: // Tilt
                    TCP_Server_Send($"{{[ACK][Tilt][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                    return true;
                case 2: // Zoom
                    TCP_Server_Send($"{{[ACK][Zoom][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                    return true;
                case 3: // Position GoTo
                    string[] xyz = data[1].Split(',');
                    TCP_Server_Send($"{{[ACK][Position][{data[0]}][{Int32.Parse(xyz[0])},{Int32.Parse(xyz[1])},{Int32.Parse(xyz[2])}]}}\n");
                    return true;
                case 4: //Preset Recall
                    TCP_Server_Send($"{{[ACK][Recall][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                    return true;
                case 5: // Preset save
                    TCP_Server_Send($"{{[ACK][Save][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Function to execute room control commands from the EMS software
        /// </summary>
        /// <param name="command">Int command from Room_Function to execute</param>
        /// <param name="data">String list of data needed to execute the command</param>
        /// <returns>Boolean as to whether the command was executed or not</returns>
        public static bool Room_Command(int command, List<string> data) {
            Boolean processed = false;
            Boolean all_rooms = false;
            int RMID = -1;
            if (data[0] == "0") {
                all_rooms = true;
                RMID = 0;
            }

            if (!data[0].Contains(",")) { // ie [status][data[0] = "RM01"][3]... EMS_Parse passed only part of command to Room_Command at a time.
                                          // ie [status][0][3]
                switch (command) {
                    case 0: // Status
                        if (all_rooms == true) {
                            TCP_Server_Send($"{{[ACK][Status][ALL ROOMS][{Int32.Parse(data[1])}]}}\n");
                        } else {
                            TCP_Server_Send($"{{[ACK][Status][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                        }
                        Set_Room_States_Event(data[0], data[1], "status", all_rooms);
                        return true;
                    case 1: // Privacy
                        if (all_rooms == true) {
                            TCP_Server_Send($"{{[ACK][Privacy][ALL ROOMS][{Int32.Parse(data[1])}]}}\n");
                        } else {
                            TCP_Server_Send($"{{[ACK][Privacy][{data[0]}][{Int32.Parse(data[1])}]}}\n");
                        }
                        Set_Room_States_Event(data[0], data[1], "privacy", all_rooms);
                        return true;
                    case 2: // Mute
                        if (data[0] != "0") { 
                            RMID = Int32.Parse(data[0].Substring(2)); // "RM01" to 1
                        } 
                        if (Int32.Parse(data[1]) == 1) {
                            Biamp_Mute_Update(RMID, true); 
                        } else {
                            Biamp_Mute_Update(RMID, false);
                        }
                        Set_Room_States_Event(data[0], data[1], "mute", all_rooms);
                        return true;
                    default:
                        return false;
                }
            } else {
                foreach (string roomID in data[0].Split(',')) {// ie [status]["RM01,RM02"][3]...EMS_Parse passed only part of command to Room_Command at a time.
                    switch (command) {
                        case 0: // Status
                            TCP_Server_Send($"{{[ACK][Status][{roomID}][{Int32.Parse(data[1])}]}}\n");
                            processed = true;
                            Set_Room_States_Event(roomID, data[1], "status", all_rooms);
                            break;
                        case 1: // Privacy
                            TCP_Server_Send($"{{[ACK][Privacy][{roomID}][{Int32.Parse(data[1])}]}}\n");
                            Set_Room_States_Event(roomID, data[1], "privacy", all_rooms);
                            processed = true;
                            break;
                        case 2: // Mute
                            RMID = Int32.Parse(roomID.Substring(2));
                            if (Int32.Parse(data[1]) == 1) {
                                Biamp_Mute_Update(RMID, true);
                            } else {
                                Biamp_Mute_Update(RMID, false);
                            }
                            Set_Room_States_Event(roomID, data[1], "mute", all_rooms);
                            processed = true;
                            break;
                        default:
                            break;
                    }
                }
            }
            return processed;
        }

        /// <summary>
        /// Function to execute paging routing and preset commands from the EMS software
        /// </summary>
        /// <param name="command">Int command from Paging_Function to execute</param>
        /// <param name="data">String list of data needed to execute the command</param>
        /// <returns>Boolean as to whether the command was executed or not</returns>
        public static bool Paging_Command(int command, List<string> data) {
            Boolean all_rooms = false;
            if (data[1] == "0") {
                all_rooms = true;
            }
                switch (command) {
                case 0: 
                    //{[Paging][Route][6][6]}
                    //{[Paging][Route][6][0]}
                    if (!data[1].Contains(",")) {
                        Biamp_Update(Int32.Parse(data[0]), Int32.Parse(data[1]), true, all_rooms);
                    } else {
                        //{[Paging][Route][6][1,2]}
                        foreach (string i in data[1].Split(',')) {
                            Biamp_Update(Int32.Parse(data[0]), Int32.Parse(i), true, all_rooms);
                        }
                    }
                    return true;
                case 1: // Zone
                    Biamp_Preset($"Input_{data[0]}_{data[1]}");
                    return true;
                case 2: // Clear
                    if (data[1] == "0") {
                        Biamp_Preset($"Input_{data[0]}_Clear");
                    } else {
                        //{[Paging][Clear][6][6]}
                        if (!data[1].Contains(",")) {
                            Biamp_Update(Int32.Parse(data[0]), Int32.Parse(data[1]), false, all_rooms);
                        } else {
                            //{[Paging][Clear][6][6,7]}
                            foreach (string i in data[1].Split(',')) {
                                Biamp_Update(Int32.Parse(data[0]), Int32.Parse(i), false, all_rooms);
                            }
                        }
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Function to execute message player commands from the EMS software
        /// </summary>
        /// <param name="command">Int command from Message_Function to execute</param>
        /// <param name="data">String list of data needed to execute the command</param>
        /// <returns>Boolean as to whether the command was executed or not</returns>
        public static bool Message_Command(int command, List<string> data) {
            switch (command) {
                case 0: // Play
                    Message_Play(UInt32.Parse(data[0]), UInt32.Parse(data[1]));
                    return true;
                case 1: // Record
                    Message_Record(UInt32.Parse(data[0]), UInt32.Parse(data[1]));
                    return true;
                case 2: // Delete
                    Message_Delete(UInt32.Parse(data[0]), UInt32.Parse(data[1]));
                    return true;
                case 3: // Stop
                    Message_Stop(UInt32.Parse(data[0]), UInt32.Parse(data[1]));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Function to execute request commands from the EMS software
        /// </summary>
        /// <param name="command">Int command from Request_Function to execute</param>
        /// <param name="data">String list of data needed to execute the command</param>
        /// <returns>Boolean as to whether the command was executed or not</returns>
        public static bool Request_Command(int command, List<string> data) {
            switch (command) {
                case 0: // Status
                    EmsToCrestronStatus(data);
                    return true;
                case 1: // Privacy

                    return true;

                case 2: // Mute

                    return true;
                case 3: // Route

                    return true;
                case 4: // Position

                    return true;
                case 5: // Ping
                    Request_Ping();
                    return true;
                default:
                    return false;
            }
        }

        public static bool ACK_Response(int command, List<string> data) {
            switch (command) {
                case 0: // Status

                    return true;
                case 1: // Privacy

                    return true;

                case 2: // Mute

                    return true;
                case 3: // Route

                    return true;
                case 4: // Position

                    return true;
                case 5: // Ping
                    Request_Ping();
                    return true;
                default:
                    return false;
            }
        }

        public static Thread TCP_Server_Start(string ip_address, int port) {
            if (TCP_Server_Thread == null) {
                TCP_Server = new TcpListener(IPAddress.Parse(ip_address), port);
                TCP_Server.Start();
                TCP_Server_Thread = new Thread(() => TCP_Server_Listener());
                TCP_Server_Thread.Start();
            }
            return TCP_Server_Thread;
        }

        public static void TCP_Server_Stop() {
            if (TCP_Server_Thread != null) {
                for (int i = 0; i < TCP_Server_Clients.Length; i++) {
                    if (TCP_Server_Clients[i] != null) {
                        try {
                            TCP_Server_Clients[i].Close();
                        } catch {
                        }
                        TCP_Server_Clients[i] = null;
                    }
                }
                // Stop the thread
                TCP_Server_Thread.Abort();

                // Stop the TCP_Server Object: Closes the port.
                if (TCP_Server != null) {
                    Debug_Print("Stopping Port");
                    TCP_Server.Stop(); // Close the listener and release the port
                }

            }
        }

        public static void TCP_Server_Send(string data) {
            if (TCP_Server_Thread != null) {
                byte[] msg = System.Text.Encoding.ASCII.GetBytes(data + DELIMITER);

                // Send to the TCP
                foreach (TcpClient client in TCP_Server_Clients) {
                    if (client != null && client.Connected) {
                        client.GetStream().Write(msg, 0, msg.Length);

                        Debug_Print($"TCP Sending: {msg}");
                    }
                }
            }
        }

        public static bool TCP_Server_Get_Status(int index) {
            if (TCP_Server_Clients[index] != null) {
                return TCP_Server_Clients[index].Connected;
            }
            return false;
        }

        private static void TCP_Server_Listener() {
            while (true) {
                if (TCP_Server_Clients[0] == null || !TCP_Server_Clients[0].Connected) {
                    //Debug_Print("Wait on index: 0");
                    TCP_Server_Clients[0] = TCP_Server.AcceptTcpClient();
                    Debug_Print("Client connected on index: 0, to our");
                    client_thread[0] = new Thread(() => TCP_Server_Client_Handler(TCP_Server_Clients[0]));
                    client_thread[0].Start();
                } else if (TCP_Server_Clients[1] == null || !TCP_Server_Clients[1].Connected) {
                    Debug_Print("Wait on index: 1");
                    TCP_Server_Clients[1] = TCP_Server.AcceptTcpClient();
                    Debug_Print("Client connected on index: 1");
                    client_thread[1] = new Thread(() => TCP_Server_Client_Handler(TCP_Server_Clients[1]));
                    client_thread[1].Start();
                }
            }
        }

        private static void TCP_Server_Client_Handler(TcpClient client) {
            string data = null;
            Byte[] bytes = new Byte[256];

            try {
                NetworkStream TCP_Server_Stream = client.GetStream();
                int i;
                // Loop to receive all the data sent by the client.
                while ((i = TCP_Server_Stream.Read(bytes, 0, bytes.Length)) != 0) {
                    // Translate data bytes to a ASCII string.
                    data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);

                    // Raise event to pass the data received
                    TCP_Server_Receive_Data(data);
                }
                client.Close();
            } catch (Exception e) {
                Debug_Print($"Error in reading from client ({e.Data})");
                Debug_Print($"An error occurred: {e.GetType().Name} - {e.Message}");
                Debug_Print($"Stack Trace: {e.StackTrace}");
                client.Close();
            }
        }

        private static bool EMS_Client_Connected() {
            if (EMS_Client == null || !EMS_Client.Connected) {
                return false;
            }
            //return true;
            //if (EMS_Client.Client == null) { // Add this check
                //return false;
            //}
            return !EMS_Client.Client.Poll(500, SelectMode.SelectRead);
        }

        public static void EMS_Client_Disconnect() {
            try {
                EMS_Client.Close();
                Debug_Print("Client Disconnected");
            } catch (Exception e) {
                Debug_Print($"Error closing TCP Client: {e.Message}");
            }
        }

        public static void EMS_Client_Send(string data, string ip_address, int port) {
            //Debug_Print($"Client state: {EMS_Client_Connected()}");
            //if(EMS_Client != null)Debug_Print($"Poll: {EMS_Client.Client.Poll(500, SelectMode.SelectRead)}");
            if (!EMS_Client_Connected()) {
                Debug_Print($"Making new connection to {ip_address} on port {port}");
                EMS_Client_Connect(ip_address, port);
            } else {
                // Client connected already
                Debug_Print("already connected");
                //if(EMS_Timeout_Thread == null || !EMS_Timeout_Thread.IsAlive) {
                //    EMS_Client_Start_Time = DateTime.Now;
                //    EMS_Timeout_Thread = new Thread(EMS_Timeout);
                //    EMS_Timeout_Thread.Start();
                //}
                //if(EMS_Client_Thread == null || !EMS_Client_Thread.IsAlive) {
                //    EMS_Client_Thread = new Thread(EMS_Receiver);
                //    EMS_Client_Thread.Start();
                //}
            }
            // Send Data to Server
            try {
                EMS_Client_Start_Time = DateTime.Now;
                byte[] msg = System.Text.Encoding.ASCII.GetBytes(data + DELIMITER);
                Debug_Print($"Sending command: {data}");
                EMS_Client_Stream.Write(msg, 0, msg.Length);
            } catch (Exception e) {
                Debug_Print($"Sending error: {e.Data}");
                EMS_Client_Disconnect();
            }
        }

        private static void EMS_Client_Connect(string ip_address, int port) {
            try {
                // Connect to TCP
                Thread temp = new Thread(() => EMS_Client_Connect_Thread(ip_address, port));
                temp.Start();
                ; Thread.Sleep(500);

                // Start up threads
                EMS_Client_Thread = new Thread(EMS_Receiver);
                EMS_Client_Thread.Start();

                EMS_Client_Start_Time = DateTime.Now;
                EMS_Timeout_Thread = new Thread(EMS_Timeout);
                EMS_Timeout_Thread.Start();

                Debug_Print("Client Connected To EMS");
            } catch {
                Debug_Print("Connection failed");
            }
        }

        private static void EMS_Client_Connect_Thread(string ip_address, int port) {
            try {
                EMS_Client = new TcpClient();
                EMS_Client.Connect(ip_address, port); 
                Console.WriteLine("Connection successful.");
            } catch (SocketException ex) {
                Debug_Print($"SocketException: {ex.Message}");
                Debug_Print("Connection failed");
                return;
            } catch (Exception ex) {
                Debug_Print($"Exception: {ex.Message}");
                Debug_Print("Connection failed");
                return;
            }
            EMS_Client_Stream = EMS_Client.GetStream();
        }

        private static void EMS_Timeout() {
            Debug_Print("Starting timeout");
            while (EMS_Client.Connected) {
                // If clients connected, and it's been DURATION seconds with an empty queue, then disconnect
                if ((DateTime.Now - EMS_Client_Start_Time).TotalSeconds > DURATION) {
                    EMS_Client_Disconnect();
                    break;
                }
            }
            Debug_Print("Timeout over");
        }

        private static void EMS_Receiver() {
            while (EMS_Client.Connected) {
                try {
                    byte[] buffer = new byte[EMS_Client.ReceiveBufferSize];
                    int bytesRead = EMS_Client_Stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead > 0) {
                        string receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        EMS_Client_Receive_Data(receivedData);
                    }
                } catch (Exception e) {
                    // Failed to read TCP Buffer
                    Debug_Print($"Failed reading client buffer: {e.Message}");
                }
            }
            Debug_Print("Receiving over");
        }
    }

    public class EMS_Camera {
        private string ip_address = "0.0.0.0";
        private int max_pan_speed = 5;
        private int max_tilt_speed = 5;
        private int max_zoom_speed = 5;
        private int hold_time = 4;

        private Thread presetHoldThread;
        private DateTime Preset_Press_Time;
        private bool presetHeld;

        public static event tcpDataSend TCP_Client_Send;
        public static event cameraControl Preset_Saved;

        public EMS_Camera(string ip_address, int max_pan_speed = 5, int max_tilt_speed = 5, int max_zoom_speed = 5) {
            this.ip_address = ip_address;
            this.max_pan_speed = max_pan_speed;
            this.max_tilt_speed = max_tilt_speed;
            this.max_zoom_speed = max_zoom_speed;
        }

        public EMS_Camera(string ip_address) {
            this.ip_address = ip_address;
            this.max_pan_speed = 5;
            this.max_tilt_speed = 5;
            this.max_zoom_speed = 5;
        }

        public string get_IP() {
            return ip_address;
        }

        public void Tilt(int speed) {
            // If speed is above max, lower it to the max.
            if (Math.Abs(speed) > max_tilt_speed) speed = Math.Sign(speed) * max_tilt_speed;
            TCP_Client_Send($"{{[Camera][Tilt][{ip_address}][{speed}]}}");
        }

        public void Pan(int speed) {
            if (Math.Abs(speed) > max_pan_speed) speed = Math.Sign(speed) * max_pan_speed;
            TCP_Client_Send($"{{[Camera][Pan][{ip_address}][{speed}]}}");
        }

        public void Zoom(int speed) {
            if (Math.Abs(speed) > max_zoom_speed) speed = Math.Sign(speed) * max_zoom_speed;
            TCP_Client_Send($"{{[Camera][Zoom][{ip_address}][{speed}]}}");
        }

        public void Preset_Press(int number) {
            presetHoldThread = new Thread(() => Preset_Thread(number));
            presetHoldThread.Start();
        }

        private void Preset_Thread(int number) {
            Preset_Press_Time = DateTime.Now;
            presetHeld = false;
            while ((DateTime.Now - Preset_Press_Time).TotalSeconds < hold_time) { }
            presetHeld = true;
            Preset_Saved($"{number}");
        }

        public void Preset_Release(int number) {
            if (presetHeld) {
                Save_Preset(number);
            } else {
                Recall_Preset(number);
                if (presetHoldThread != null && presetHoldThread.IsAlive) {
                    presetHoldThread.Abort();
                }
            }
        }

        public void Recall_Preset(int number) {
            TCP_Client_Send($"{{[Camera][Recall][{ip_address}][{number}]}}");
        }

        public void Save_Preset(int number) {
            TCP_Client_Send($"{{[Camera][Save][{ip_address}][{number}]}}");
        }

        public void Set_Position(int x, int y, int z) {
            TCP_Client_Send($"{{[Camera][Position][{ip_address}][{x},{y},{z}]}}");
        }
    }

    public class EMS_Room {
        private string room_id;
        private int state;
        private int mute;

        public static event tcpDataSend TCP_Client_Send;

        public EMS_Room(string room_id) {
            this.room_id = room_id;
            state = 2;
            mute = 0;
        }

        public void Request_Privacy() {
            TCP_Client_Send($"{{[Room][Privacy][{room_id}][1]}}");
        }

        public void Leave_Privacy() {
            TCP_Client_Send($"{{[Room][Privacy][{room_id}][0]}}");
        }

        public void Start_Recording() {
            TCP_Client_Send($"{{[Room][Status][{room_id}][1]}}");
        }

        public void Stop_Recording() {
            TCP_Client_Send($"{{[Room][Status][{room_id}][2]}}");
        }

        public void Pause_Recording() {
            TCP_Client_Send($"{{[Room][Status][{room_id}][3]}}");
        }

        public void Mute_Room() {
            TCP_Client_Send($"{{[Room][Mute][{room_id}][1]}}");
        }
        public void UnMute_Room() {
            TCP_Client_Send($"{{[Room][Mute][{room_id}][0]}}");
        }

        public int Get_State() {
            return state;
        }
        public void Set_State(int state) {
            this.state = state;
        }
        public int Get_Mute_State() {
            return mute;
        }
        public void Set_Mute_State(int mute) {
            this.mute = mute;
        }
    }
}
