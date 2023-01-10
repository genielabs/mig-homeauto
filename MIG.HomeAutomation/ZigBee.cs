using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MIG.Config;
using MIG.Interfaces.HomeAutomation.Commons;
using ZigBeeNet;
using ZigBeeNet.App.Basic;
using ZigBeeNet.App.Discovery;
using ZigBeeNet.App.IasClient;
using ZigBeeNet.DataStore.Json;
using ZigbeeNet.Hardware.ConBee;
using ZigBeeNet.Security;
using ZigBeeNet.Tranport.SerialPort;
using ZigBeeNet.Transaction;
using ZigBeeNet.ZCL;
using ZigBeeNet.ZCL.Clusters;
using ZigBeeNet.ZCL.Clusters.LevelControl;
using ZigBeeNet.ZCL.Clusters.OnOff;
using ZigBeeNet.ZDO.Command;
using ZigBeeNet.ZDO.Field;

namespace MIG.Interfaces.HomeAutomation
{

    public class ZigBee : MigInterface
    {

        #region MigInterface API commands and events
        
        public enum Commands
        {
            NotSet,

            Controller_Discovery,
            Controller_NodeAdd,
            Controller_NodeRemove,
            Controller_SoftReset,
            Controller_HardReset,
            Controller_HealNetwork,
            Controller_NodeNeighborUpdate,
            Controller_NodeRoutingInfo,

            Control_On,
            Control_Off,
            Control_Level,
            Control_Toggle,

            Thermostat_ModeGet,
            Thermostat_ModeSet,
            Thermostat_SetPointGet,
            Thermostat_SetPointSet,
            Thermostat_FanModeGet,
            Thermostat_FanModeSet,
            Thermostat_FanStateGet,
            Thermostat_OperatingStateGet,

            DoorLock_Set,
            DoorLock_Get
        }

        // ZigBee specific events
        const string EventPath_WakeUpInterval
            = "ZigBeeNode.WakeUpInterval";
        const string EventPath_Battery
            = "ZigBeeNode.Battery";
        const string EventPath_NodeInfo
            = "ZigBeeNode.NodeInfo";
        const string EventPath_RoutingInfo
            = "ZigBeeNode.RoutingInfo";
        const string EventPath_ManufacturerSpecific
            = "ZigBeeNode.ManufacturerSpecific";
        const string EventPath_VersionReport
            = "ZigBeeNode.VersionReport";

        #endregion
        
        #region Private fields

        private const int NumberOfAddAttempts = 60;
        private const int NumberOfRemoveAttempts = 60;
        private const int DelayBetweenAttempts = 500;

        private ZigBeeNetworkManager networkManager;
        private ZigBeeSerialPort zigbeePort;
        private ZigbeeDongleConBee zigbeeDongle;

        private ushort lastRemovedNode;
        private ushort lastAddedNode;

        #endregion
        
        #region MIG Interface members

        public event InterfaceModulesChangedEventHandler InterfaceModulesChanged;
        public event InterfacePropertyChangedEventHandler InterfacePropertyChanged;

        public bool IsEnabled { get; set; }

        public List<Option> Options { get; set; }

        public void OnSetOption(Option option)
        {
            if (IsEnabled)
                Connect();
        }

        List<InterfaceModule> modules = new List<InterfaceModule>();
        public List<InterfaceModule> GetModules()
        {
            return modules;
        }

        public bool IsConnected
        {
            get
            {
                return networkManager != null && networkManager.NetworkState == ZigBeeNetworkState.ONLINE;
            }
        }

        public object InterfaceControl(MigInterfaceCommand request)
        {
            var returnValue = new ResponseStatus(Status.Ok);
            bool raiseEvent = false;
            string eventParameter = ModuleEvents.Status_Level;
            string eventValue = "";

            ushort nodeId = ushort.Parse(request.Address);

            // controller commands

            if (nodeId == 0) 
            {

                switch (request.Command)
                {
                    case "Controller.NodeAdd":
                        lastAddedNode = 0;
                        // Enable node pairing
                        ZigBeeNode coord = networkManager.GetNode(0);
                        Console.WriteLine("Joining enabled...");
                        coord.PermitJoin(true);
                        for (int i = 0; i < NumberOfAddAttempts; i++)
                        {
                            if (lastAddedNode > 0)
                            {
                                break;
                            }

                            Thread.Sleep(DelayBetweenAttempts);
                        }
                        coord.PermitJoin(false);
                        returnValue = new ResponseStatus(Status.Ok, lastAddedNode.ToString());
                        break;
                }

                return returnValue;
            }
            
            // node commands

            var module = modules.Find((m) => m.Address == nodeId.ToString(CultureInfo.InvariantCulture));
            if (module == null)
            {
                return new ResponseStatus(Status.Error, $"Unknown node {nodeId}.");
            }
            var nodeData = module.CustomData as ZigBeeNodeData;

            var node = networkManager.GetNode(nodeId);
            if (node == null)
            {
                return new ResponseStatus(Status.Error, "Could not get node instance.");
            }
            // Get node endpoint
            var endpoint = node.GetEndpoints().FirstOrDefault();
            if (endpoint == null)
            {
                return new ResponseStatus(Status.Error, "Could not determine node endpoint address.");
            }
            ZigBeeEndpointAddress endpointAddress = endpoint.GetEndpointAddress();

            switch (request.Command)
            {
                case "Control.On":
                    networkManager
                        .Send(endpointAddress, new OnCommand())
                        .Wait();
                    nodeData.Level = 1;
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;
                case "Control.Off":
                    networkManager
                        .Send(endpointAddress, new OffCommand())
                        .Wait();
                    nodeData.Level = 0;
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;
                case "Control.Level":
                    var level = int.Parse(request.GetOption(0));
                    var command = new MoveToLevelWithOnOffCommand()
                    {
                        Level = (byte)(level*2.55D),
                        TransitionTime = 10
                    };
                    networkManager
                        .Send(endpointAddress, command)
                        .Wait();
                    nodeData.Level = (level / 100D);
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;
                case "Control.Toggle":
                    networkManager
                        .Send(endpointAddress, new ToggleCommand())
                        .Wait();
                    nodeData.Level = nodeData.Level > 0 ? 0 : nodeData.LastLevel;
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;
                case "Debug.Test":
                    TestNode(nodeId, endpointAddress, endpoint);
                    break;
            }

            if (raiseEvent)
            {
                OnInterfacePropertyChanged(this.GetDomain(), nodeId.ToString(CultureInfo.InvariantCulture), "ZigBee Node", eventParameter, eventValue);
            }
            return returnValue;
        }

        public bool Connect()
        {
            Disconnect();
            string portName = this.GetOption("Port").Value;
            if (String.IsNullOrEmpty(portName))
            {
                return false;
            }
            
            zigbeePort = new ZigBeeSerialPort(portName, 115200);
            zigbeeDongle = new ZigbeeDongleConBee(zigbeePort);
            networkManager = new ZigBeeNetworkManager(zigbeeDongle);

            var dataStore = new JsonNetworkDataStore(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "mig", "zigbee")
            );
            networkManager.SetNetworkDataStore(dataStore);
            // Add discovery extension
            ZigBeeDiscoveryExtension discoveryExtension = new ZigBeeDiscoveryExtension();
            discoveryExtension.SetUpdatePeriod(60);
            networkManager.AddExtension(discoveryExtension);

            // Initialise the network
            networkManager.Initialize();

            // Add events listeners
            networkManager.AddCommandListener(new ZigBeeTransaction(networkManager));
            networkManager.AddCommandListener(new ConsoleCommandListener());
            networkManager.AddNetworkNodeListener(new ConsoleNetworkNodeListener(this));
            // Add other extensions
            networkManager.AddSupportedClientCluster(ZclOnOffCluster.CLUSTER_ID);
            networkManager.AddSupportedClientCluster(ZclColorControlCluster.CLUSTER_ID);
            networkManager.AddExtension(new ZigBeeBasicServerExtension());
            networkManager.AddExtension(new ZigBeeIasCieExtension());

            // Start network manager
            ZigBeeStatus startupSucceded = networkManager.Startup(false);
            

            // get stored node list
            for (int i = 0; i < networkManager.Nodes.Count; i++)
            {
                var node = networkManager.Nodes[i];
                AddNode(node);
                Console.WriteLine($"{i}. {node.LogicalType}: {node.NetworkAddress}");
            }

            if (startupSucceded == ZigBeeStatus.SUCCESS)
            {
                return true;
            }

            return false;
        }

        public void Disconnect()
        {
            if (networkManager != null && networkManager.Transport != null)
            {
                zigbeePort.Close();
                zigbeeDongle.Shutdown();
                networkManager.Shutdown();
                zigbeePort = null;
                zigbeeDongle = null;
                networkManager = null;
            }
        }

        public bool IsDevicePresent()
        {
            return true;
        }

        #endregion

        protected virtual void OnInterfaceModulesChanged(string domain)
        {
            if (InterfaceModulesChanged != null)
            {
                var args = new InterfaceModulesChangedEventArgs(domain);
                InterfaceModulesChanged(this, args);
            }
        }
        protected virtual void OnInterfacePropertyChanged(string domain, string source, string description, string propertyPath, object propertyValue)
        {
            if (InterfacePropertyChanged != null)
            {
                var args = new InterfacePropertyChangedEventArgs(domain, source, description, propertyPath, propertyValue);
                new Thread(() =>
                {
                    InterfacePropertyChanged(this, args);
                }).Start();
            }
        }

        public void AddNode(ZigBeeNode node)
        {
            if (node.IsFullFunctionDevice && node.LogicalType == NodeDescriptor.LogicalType.ROUTER)
            {
                modules.Add(new InterfaceModule()
                {
                    Domain = "HomeAutomation.ZigBee",
                    Address = node.NetworkAddress.ToString(CultureInfo.InvariantCulture),
                    // TODO: get device type 
                    ModuleType = ModuleTypes.Dimmer,
                    CustomData = new ZigBeeNodeData()
                });
                lastAddedNode = node.NetworkAddress;
                OnInterfacePropertyChanged(this.GetDomain(), "1", "ZigBee Controller", "Controller.Status", "Added node " + node.NetworkAddress);
                OnInterfaceModulesChanged(this.GetDomain());
            }
        }

        public void RemoveNode(ZigBeeNode node)
        {
            int removed = modules.RemoveAll((m) => m.Address == node.NetworkAddress.ToString(CultureInfo.InvariantCulture));
            if (removed > 0)
            {
                lastRemovedNode = node.NetworkAddress;
                OnInterfacePropertyChanged(this.GetDomain(), "1", "ZigBee Controller", "Controller.Status", "Removed node " + node.NetworkAddress);
                OnInterfaceModulesChanged(this.GetDomain());
            }
        }

        private bool ResetNetwork()
        {
            bool resetNetwork = false;
            if (resetNetwork)
            {
                //TODO: make the network parameters configurable
                ushort panId = 1;
                ExtendedPanId extendedPanId = new ExtendedPanId();
                ZigBeeChannel channel = ZigBeeChannel.CHANNEL_11;
                ZigBeeKey networkKey = ZigBeeKey.CreateRandom();
                ZigBeeKey linkKey = new ZigBeeKey(new byte[] { 0x5A, 0x69, 0x67, 0x42, 0x65, 0x65, 0x41, 0x6C, 0x6C, 0x69, 0x61, 0x6E, 0x63, 0x65, 0x30, 0x39 });

                Console.WriteLine($"*** Resetting network");
                Console.WriteLine($"  * PAN ID           = {panId}");
                Console.WriteLine($"  * Extended PAN ID  = {extendedPanId}");
                Console.WriteLine($"  * Channel          = {channel}");
                Console.WriteLine($"  * Network Key      = {networkKey}");
                Console.WriteLine($"  * Link Key         = {linkKey}");

                networkManager.SetZigBeeChannel(channel);
                networkManager.SetZigBeePanId(panId);
                networkManager.SetZigBeeExtendedPanId(extendedPanId);
                networkManager.SetZigBeeNetworkKey(networkKey);
                networkManager.SetZigBeeLinkKey(linkKey);
            }
            return networkManager.Startup(resetNetwork) == ZigBeeStatus.SUCCESS;
        }

        private void TestNode(ushort nodeId, ZigBeeEndpointAddress endpointAddress, ZigBeeEndpoint endpoint)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            /*
            CheckNode(node, endpoint)
                .Wait();
            */
            NodeDescriptorRequest nodeDescriptorRequest = new NodeDescriptorRequest()
            {
                DestinationAddress = endpointAddress,
                NwkAddrOfInterest = nodeId
            };
            networkManager.SendTransaction(nodeDescriptorRequest);

            ReadClusterData(endpoint)
                .Wait();
            ReadClusterLevelData(endpoint)
                .Wait();
            DiscoverAttributes(endpoint)
                .Wait();
            watch.Stop();
            var elapsedMs = watch.ElapsedMilliseconds;
            Console.WriteLine($"Commands took {elapsedMs}ms");
        }
        
        private async Task CheckNode(ZigBeeNode node, ZigBeeEndpoint endpoint)
        {
            foreach (int clusterId in endpoint.GetInputClusterIds())
            {
                ZclCluster cluster = endpoint.GetInputCluster(clusterId);
                cluster.DiscoverAttributes(true).Wait();
                if (!await cluster.DiscoverAttributes(true))
                    Console.WriteLine("Error while discovering attributes for cluster {0}", cluster.GetClusterName());
            }
        }

        private async Task ReadClusterData(ZigBeeEndpoint endpoint)
        {
            var cluster = endpoint.GetInputCluster(ZclBasicCluster.CLUSTER_ID);
            if (cluster != null)
            {
                string manufacturerName = (string)(await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_MANUFACTURERNAME));
                string model = (string)(await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_MODELIDENTIFIER));
                Console.WriteLine($"Manufacturer Name = {manufacturerName}");
                Console.WriteLine($"Model identifier = {model}");
            }
        }

        private async Task ReadClusterLevelData(ZigBeeEndpoint endpoint)
        {
            var cluster = endpoint.GetInputCluster(ZclLevelControlCluster.CLUSTER_ID);
            if (cluster != null)
            {
                byte level = (byte)(await cluster.ReadAttributeValue(ZclLevelControlCluster.ATTR_CURRENTLEVEL));
                Console.WriteLine($"Current level = {level}");
            }
        }

        private async Task DiscoverAttributes(ZigBeeEndpoint endpoint)
        {
            foreach (int clusterId in endpoint.GetInputClusterIds())
            {
                ZclCluster cluster = endpoint.GetInputCluster(clusterId);
                if (!await cluster.DiscoverAttributes(true))
                    Console.WriteLine("Error while discovering attributes for cluster {0}", cluster.GetClusterName());
            }
        }

    }


    public class ZigBeeNodeData
    {
        private double level = 0;
        public double Level {
            get => level;
            set
            {
                level = value;
                if (level != 0)
                {
                    LastLevel = level;
                }
            }
        }
        public double LastLevel = 0;
    }
    
    public class ConsoleCommandListener : IZigBeeCommandListener
    {
        public void CommandReceived(ZigBeeCommand command)
        {

            // TODO: parse / handle received commands
            
            Console.WriteLine($"\n\n{command}\n");
        }
    }

    public class ConsoleNetworkNodeListener : IZigBeeNetworkNodeListener
    {
        private ZigBee zigBee;
        public ConsoleNetworkNodeListener(ZigBee zigBeeInterface)
        {
            zigBee = zigBeeInterface;
        }
        public void NodeAdded(ZigBeeNode node)
        {
            Console.WriteLine("Node " + node.IeeeAddress + " added " + node);
            if (node.NetworkAddress != 0)
            {
                zigBee.AddNode(node);
            }
        }

        public void NodeRemoved(ZigBeeNode node)
        {
            Console.WriteLine("Node removed " + node);
            zigBee.RemoveNode(node);
        }

        public void NodeUpdated(ZigBeeNode node)
        {
            Console.WriteLine("Node updated " + node);
            // TODO: ...
        }
    }
}
