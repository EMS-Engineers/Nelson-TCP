using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Threading
using System.Threading;

// For Ssh connection
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BiampLibrary {

    public delegate void debugHandler2(string data);

    public abstract class AbstractBiampObject {
        public abstract void Subscribe();
        public abstract string Change(string command);
        public abstract bool CheckIfOurs(string command, string lastCommand);
        public abstract string PrintLine();
    }

    public class Biamp {

        // Variables
        Queue<string> commandQueue = new Queue<string>();
        List<AbstractBiampObject> subscribedObjects = new List<AbstractBiampObject>();
        string ipAddress;
        string username;
        string password;
        KeyboardInteractiveAuthenticationMethod authMethod;
        SshClient client;
        ShellStream shell;
        string lastCommand;

        // Events 
        public delegate void DataReceivedHandler(string command);
        public event DataReceivedHandler DataReceivedEvent;
        public delegate void ObjectEventHandler(AbstractBiampObject obj, string value);
        public event ObjectEventHandler ObjectFeedbackEvent;

        public static event debugHandler2 Debug_Print;

        public Biamp(string ip, string user, string pwd) {
            this.ipAddress = ip;
            this.username = user;
            this.password = pwd;

            // Use method simulated interacting with keyboard to enter username.  Server then prompts for password.
            authMethod = new KeyboardInteractiveAuthenticationMethod(username);
            authMethod.AuthenticationPrompt += (sender, e) => {
                foreach (var prompt in e.Prompts) {
                    prompt.Response = password;
                }
            };
        }

        public bool isConnected() {
            return client.IsConnected;
        }

        public bool ConnectAndStartThreads() {

            ConnectionInfo connectionInfo = new ConnectionInfo(this.ipAddress, this.username, authMethod);
            client = new SshClient(connectionInfo);
            client.Connect();

            if (client.IsConnected) {
                Debug_Print("Biamp library client is connected to Biamp.");
                
                // Create a shell stream (act as our terminal)
                shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 1024);
                Debug_Print("Biamp client connected to shell.");

                // Create a new continuous thread to listen for incoming data from server
                Thread whileLoopThread = new Thread(ListenForData);
                whileLoopThread.Start();

                // Create a thread to send data to the biamp with a delay.
                Thread queueThread = new Thread(SendDataQueue);
                queueThread.Start();
                return true;
            } 
            else {
                throw new Exception("Biamp library client not connected.");
            }
        }
        public void Subscribe(AbstractBiampObject obj) {
            subscribedObjects.Add(obj);
        }
        public bool Disconnect() {
            try {
                client.Disconnect();
                Debug_Print("Biamp Library client is disconnected.");
                return true;
            }
            catch {
                return false;
            }
        }
        public string grabLinesValue(string feedbackLine) {
            // Return value (last parameter of the feedback line)
            string[] args = feedbackLine.Split(':');
            string value = args[args.Length - 1];
            if (value.Substring(0, 4) == "true") {
                return "true";
            } else if (value.Substring(0, 5) == "false") {
                return "false";
            }
            // The integer
            return value;
        }
        public void SendData(string command) {
            commandQueue.Enqueue(command);
            //Debug_Print("\nAdded to the queue: \n");
            //Debug_Print(command);
            //Debug_Print("\nqueue Length: \n");
            //Debug_Print(command.Length.ToString());
        }
        public void SendDataQueue() {
            while (client.IsConnected) {
                try {
                    if (commandQueue.Count != 0) {
                        string nextCommand = commandQueue.Dequeue();
                        shell.WriteLine(nextCommand);
                        lastCommand = nextCommand;
                        Thread.Sleep(500);
                    }
                } catch (Exception e) {
                    break;
                }
            }
            // Don't want to leave shell open in our memory, accumulating, or server side may think shell is still active.
            shell.Dispose();
            client.Disconnect();
            // In C# this thread should end w/o lingering resources on it's own!
        }
        private void ListenForData() {
            while (client.IsConnected) {
                try {
                    string feedbackLine = shell.ReadLine();
                    if (feedbackLine != null && feedbackLine != "") {

                        // This line needed just to handle presets.
                        DataReceivedEvent(feedbackLine);

                        foreach (AbstractBiampObject obj in subscribedObjects) {
                            if (obj.CheckIfOurs(feedbackLine, lastCommand)) {
                                lastCommand = "";
                                ObjectFeedbackEvent(obj, obj.Change(feedbackLine));
                            }
                        }
                    }
                } catch (Exception e) {
                    break;
                }
            }
            shell.Dispose();
            client.Disconnect();
        }
        public void RecallPresetByName(string index) {
            Debug_Print("RecallPresetByName called");
            SendData("DEVICE recallPresetByName " + index);
        }
    }
    public class BiampLevel : AbstractBiampObject {

        // Variables
        public Biamp biamp;
        string instance;
        int index;

        public BiampLevel(Biamp biamp, string instance, int index) {
            this.biamp = biamp;
            this.instance = instance;
            this.index = index;
        }
        public override bool CheckIfOurs(string feedbackLine, string lastCommand) {
            //command = '! "publishToken":"Level1_1" "value":1.000000'
            string temp = $"! \"publishToken\":\"{instance}_{index}\" \"value\":";
            return feedbackLine.Contains(temp);
        }
        public override string Change(string feedbackLine) {
            return biamp.grabLinesValue(feedbackLine);
        }
        public override void Subscribe() {
            string command = string.Format("{0} subscribe level {1} {2}_{3} 500", instance, index, instance, index);
            biamp.SendData(command);
        }
        public void SetLevel(int value) {
            string command = string.Format("{0} set level {1} {2}", instance, index, value);
            biamp.SendData(command);
        }
        public void Increment(int value) {
            string command = string.Format("{0} increment level {1} {2}", instance, index, value);
            biamp.SendData(command);
        }
        public void Decrement(int value) {
            string command = string.Format("{0} decrement level {1} {2}", instance, index, value);
            biamp.SendData(command);
        }
        public override string PrintLine() {
            return instance + "_" + index;
        }
    }
    public class BiampMute : AbstractBiampObject {
        public static event debugHandler2 Debug_Print;


        // Variables
        Biamp biamp;
        string instance;
        int index;
        public BiampMute(Biamp biamp, string instance, int index) {
            this.biamp = biamp;
            this.instance = instance;
            this.index = index;
        }
        public override bool CheckIfOurs(string feedbackLine, string lastCommand) {
            //command = '! "publishToken":"Level1_1" "value":true1'
            string temp = $"! \"publishToken\":\"{instance}_{index}\" \"value\":";
            return feedbackLine.Contains(temp);
        }
        public override string Change(string feedbackLine) {
            return biamp.grabLinesValue(feedbackLine);
        }
        public override void Subscribe() {
            //{ Instance} subscribe mute { index} { instance}_{ index} 500
            string command = string.Format("{0} subscribe mute {1} {2}_{3} 500", instance, index, instance, index);
            biamp.SendData(command);
        }
        public void SetMuteOn() {
            string command = string.Format("{0} set mute {1} true", instance, index);
            biamp.SendData(command); // Send command to Biamp 
        }
        public void SetMuteOff() {
            string command = string.Format("{0} set mute {1} false", instance, index);
            biamp.SendData(command); // Send command to Biamp 
        }
        public void Toggle() {
            string command = string.Format("{0} toggle mute {1}", instance, index);
            biamp.SendData(command); // Send command to Biamp 
        }
        public override string PrintLine() {
            return instance + "_" + index;
        }
    }
    public class BiampCrosspoint : AbstractBiampObject {
        public static event debugHandler2 Debug_Print;

        // Variables
        Biamp biamp;
        string instance;
        int index1;
        int index2;
        public BiampCrosspoint(Biamp biamp, string instance, int index1, int index2) {
            this.biamp = biamp;
            this.instance = instance;
            this.index1 = index1;
            this.index2 = index2;
        }
        public override bool CheckIfOurs(string feedbackLine, string lastCommand) {
            string[] args = lastCommand.Split(' ');
            return (args[0] == instance && args[1] == "get" && args[2] == "crosspointLevelState" && int.Parse(args[3]) == index1
                && int.Parse(args[4]) == index2) && feedbackLine.Contains("OK \"value\"");
        }
        public override string Change(string feedbackLine) {
            return biamp.grabLinesValue(feedbackLine);
        }
        private void Get() {
            string command = string.Format("{0} get crosspointLevelState {1} {2}", instance, index1, index2);
            biamp.SendData(command); // Send command to Biamp 
        }
        public void SetOn() {
            string command = string.Format("{0} set crosspointLevelState {1} {2} true", instance, index1, index2);
            Debug_Print("Set on command: " + command);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        public void SetOff() {
            string command = string.Format("{0} set crosspointLevelState {1} {2} false", instance, index1, index2);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        public void Toggle() {
            string command = string.Format("{0} toggle crosspointLevelState {1} {2}", instance, index1, index2);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        // Using Get, no Subscribe()
        public override void Subscribe() {
            throw new NotImplementedException();
        }
        public override string PrintLine() {
            return instance + "_" + index1 + "_" + index2;
        }
    }
    public class BiampRouter : AbstractBiampObject {
        // Variables
        Biamp biamp;
        string instance;
        int index;
        public BiampRouter(Biamp biamp, string instance, int index) {
            this.biamp = biamp;
            this.instance = instance;
            this.index = index;
        }
        public override bool CheckIfOurs(string feedbackLine, string lastCommand) {
            string[] args = lastCommand.Split(' ');
            return (args[0] == instance && args[1] == "get" && args[2] == "input" && int.Parse(args[3]) == index)
                && feedbackLine.Contains("OK \"value\"");
        }
        public override string Change(string feedbackLine) {
            // Return value (last parameter of the feedback line)
            string[] args = feedbackLine.Split(':');
            return args[args.Length - 1];
        }
        private void Get() {
            string command = string.Format("{0} get input {1}", instance, index);
            biamp.SendData(command); // Send command to Biamp 
        }
        public void Set(int value) {
            string command = string.Format("{0} set input {1} {2}", instance, index, value);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        public void Increment(int value) {
            string command = string.Format("{0} increment input {1} {2}", instance, index, value);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        public void Decrement(int value) {
            string command = string.Format("{0} decrement input {1} {2}", instance, index, value);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        // Using Get, no Subscribe()
        public override void Subscribe() {
            throw new NotImplementedException();
        }
        public override string PrintLine() {
            return instance + "_" + index;
        }
    }
    public class LogicState : AbstractBiampObject {
        // Variables
        Biamp biamp;
        string instance;
        int index;
        public LogicState(Biamp biamp, string instance, int index) {
            this.biamp = biamp;
            this.instance = instance;
            this.index = index;
        }
        public override bool CheckIfOurs(string feedbackLine, string lastCommand) {
            string[] args = lastCommand.Split(' ');
            return (args[0] == instance && args[1] == "get" && args[2] == "state" && int.Parse(args[3]) == index)
                && feedbackLine.Contains("OK \"value\"");
        }
        public override string Change(string feedbackLine) {
            // Return value (last parameter of the feedback line)
            string[] args = feedbackLine.Split(':');
            return args[args.Length - 1];
        }
        private void Get() {
            string command = string.Format("{0} get state {1}", instance, index);
            biamp.SendData(command); // Send command to Biamp 
        }
        public void SetOn() {
            string command = string.Format("{0} set state {1} true", instance, index);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        public void SetOff() {
            string command = string.Format("{0} set state {1} false", instance, index);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        public void Toggle() {
            string command = string.Format("{0} toggle state {1}", instance, index);
            biamp.SendData(command); // Send command to Biamp 
            Get();
        }
        // Using Get, no Subscribe()
        public override void Subscribe() {
            throw new NotImplementedException();
        }
        public override string PrintLine() {
            return instance + "_" + index;
        }
    }
}
 