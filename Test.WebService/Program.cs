/*
  This file is part of MIG (https://github.com/genielabs/mig-service-dotnet)
 
  Copyright (2012-2018) G-Labs (https://github.com/genielabs)

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

using System;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

using MIG;
using MIG.Config;

namespace Test.WebService
{
    class MainClass
    {
        public static void Main(string[] args)
        {
            var migService = new MigService();

            // Configuration can also be loaded from a file as shown below
            MigServiceConfiguration configuration;
            // Construct an instance of the XmlSerializer with the type
            // of object that is being deserialized.
            XmlSerializer mySerializer = new XmlSerializer(typeof(MigServiceConfiguration));
            // To read the file, create a FileStream.
            FileStream myFileStream = new FileStream("systemconfig.xml", FileMode.Open);
            // Call the Deserialize method and cast to the object type.
            configuration = (MigServiceConfiguration)mySerializer.Deserialize(myFileStream);
            // Set the configuration
            migService.Configuration = configuration;

            migService.StartService();

            // Enable some interfaces for testing...

            /*            
            var zwave = migService.AddInterface("HomeAutomation.ZWave", "MIG.HomeAutomation.dll");
            zwave.SetOption("Port", "/dev/ttyUSB0");
            migService.EnableInterface("HomeAutomation.ZWave");
            */

            /*
            var x10 = migService.AddInterface("HomeAutomation.X10", "MIG.HomeAutomation.dll");
            zwave.SetOption("Port", "CM19"); // "USB" for CM15 or the serial port path for CM11
            migService.EnableInterface("HomeAutomation.X10");
            */
            
            while (true)
            {
                Thread.Sleep(10000);
            }
        }
    }
}
