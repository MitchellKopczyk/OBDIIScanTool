/* For my Linux and MacOS friends compile via Mono*/
//csc Scanner.cs -r:System.Data.dll -r:Mono.Data.Sqlite.dll
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Data;
using Mono.Data.Sqlite;

namespace OBDIIToolKit
{
    //ELM327 is a basic helper class (Serial Port Controller)
    public static class ELM327
    {
        //Write a message to ELM327
        public static void Write(SerialPort port, string message)
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            port.Write(message + "\r");
        }

        //Read respone from ELM327 as a string
        public static string StringRead(SerialPort port)
        {
            int bytes = port.BytesToRead;
            char[] buffer = new char[bytes];
            port.Read(buffer, 0, bytes);
            string response = new string(buffer);
            response = Regex.Replace(response, ">", "");
            response = Regex.Replace(response, "\r", "");
            return response;
        }

        //Read respone from ELM327 as a byte array
        public static byte[] ByteRead(SerialPort port)
        {
            int bytes = port.BytesToRead;
            byte[] buffer = new byte[bytes];
            port.Read(buffer, 0, bytes);

            List<byte> list = new List<byte>();
            foreach (byte t in buffer)
            {
                if (t != 0xD && t != 0x3E)
                {
                    list.Add(t);
                }
            }
            byte[] response = list.ToArray();
            return response;
        }

        //Opitional Async Method for a desired repsonse if desried
        public static Task DesiredStringRead(SerialPort port, string desiredResult, int timeOutDurration)
        {
            return Task.Run(() =>
            {
                int currentTime = 0;

                while (true)
                {
                    if (StringRead(port) == desiredResult)
                        return;

                    if (currentTime == timeOutDurration)
                        throw new TimeoutException();
                    else
                    {
                        System.Threading.Thread.Sleep(1);
                        currentTime++;
                    }
                }
            });
        }
    }

    //Used for scanning faults on vehicle
    class Scanner
    {
        //Serial Connection
        private static SerialPort serialPort;

        //SQLite Objects 
        private static IDbConnection dbcon;
        private static IDataReader reader;

        //Path To SQLite File
        const string connectionString = "URI=file:Diagnostic Trouble Codes";

        private static bool SetProtocol()
        {
            //Request Protocol to Auto
            ELM327.Write(serialPort, "ATSP0");

            //Wait and read response
            System.Threading.Thread.Sleep(500);
            string protocol = ELM327.StringRead(serialPort);

            //Check If protocol was found ok
            if (protocol == "ATSP0OK")
                return true;
            else
                return false;
        }

        private static int NumberOfFaults()
        {
            //Request Number of stored fault code
            ELM327.Write(serialPort, "0101");

            //Wait and read response
            System.Threading.Thread.Sleep(5000);
            string getNumCodesSTR = ELM327.StringRead(serialPort);

            //Remove redundant response information
            getNumCodesSTR = getNumCodesSTR.Remove(0, 16);

            //Check if respone was correct
            if (getNumCodesSTR.Substring(0, 5) != "41 01")
                return -1;

            //Get number of code store in string than convert to int
            string numOfCodesHex = getNumCodesSTR.Substring(6, 2);
            int numberOfCodes = Convert.ToInt32(numOfCodesHex, 16);

            //number of codes is stored 7 bits and 1 bit idicates CEL
            if (numberOfCodes >= 128)
                numberOfCodes -= 128;
            return numberOfCodes;
        }

        private static void GetFaultMemory()
        {
            //Request Fault Memory
            ELM327.Write(serialPort, "03");

            //Wait and read response
            System.Threading.Thread.Sleep(5000);
            string FaultCodeDump = ELM327.StringRead(serialPort);

            //Remove the Echo
            FaultCodeDump = FaultCodeDump.Remove(0, 2);

            //Check response is correct and remove it
            if (FaultCodeDump.Substring(0, 2) != "43")
                throw new Exception();
            FaultCodeDump = FaultCodeDump.Remove(0, 2);

            //Remove all white spaces for parsing
            FaultCodeDump = Regex.Replace(FaultCodeDump, " ", "");

            //Establish Database Connection
            dbcon = new SqliteConnection(connectionString);
            dbcon.Open();
            IDbCommand dbcmd = dbcon.CreateCommand();

            //While there are code
            while (FaultCodeDump.Length >= 4)
            {
                //Get the front most fault code
                string FualtTempBuffer = FaultCodeDump.Substring(0, 4);

                //Determine cateogory of fault powertain, chasis, etc...
                string FaultCode = determineFaultCategory(FualtTempBuffer[0]);
                //Add the numerical portion to the code we will FaultCode for SQL statment
                FaultCode += FaultCodeDump.Substring(1, 3);

                string sql = "SELECT Description FROM";
                if (FaultCode[0] == 'P')
                    sql += " 'Powertrain' WHERE Code = '" + FaultCode + "'";
                else if (FaultCode[0] == 'C')
                    sql += " 'Chasis' WHERE Code = '" + FaultCode + "'";
                else if (FaultCode[0] == 'U')
                    sql += " 'Undefined' WHERE Code = '" + FaultCode + "'";
                else
                    Console.WriteLine(FaultCode + " No Description Found");

                //Ready query to be executed
                dbcmd.CommandText = sql;
                reader = dbcmd.ExecuteReader();

                //read query response and get the description of the fault code
                while(reader.Read())
                {
                    string Description = reader.GetString(1);
                    Console.WriteLine(FaultCode + " " + Description);
                }

                //Remove the Current/front most fault code from dump
                FaultCodeDump = FaultCodeDump.Remove(0, 4);
            }

            //Close Database Object and cleanup
            reader.Dispose();
            dbcmd.Dispose();
            dbcon.Close();
        }

        static void Main(string[] args)
        {
            serialPort = new SerialPort("COM3", 115200, Parity.None, 8);
            try
            {
                serialPort.Open();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: FAILED TO ESTABLISH CONNECTION WITH VECHICLES BUS :(");
                return;
            }

            Console.WriteLine("SCANNING FOR PROTOCOL...");
            if (!SetProtocol())
            {
                Console.Error.WriteLine("Error: FAILED TO SET PROTOCOL :(");
                return;
            }

            Console.WriteLine("READING FAULT MEMORY...");
            int numFaults = NumberOfFaults();
            if (numFaults < 0)
            {
                Console.Error.WriteLine("Error: FAILED TO READ FAULT MEMORY :(");
                return;
            }

            if (numFaults == 0)
            {
                Console.WriteLine("No Faults Found! :)");
                return;
            }

            GetFaultMemory();

        }
        private static void EraseCodes(SerialPort port)
        {
            ELM327.Write(port, "04");
        }

        private static string determineFaultCategory(char hex)
        {
            string decodedString = string.Empty;
            switch (hex)
            {
                case '0':
                    decodedString = "P0";
                    break;
                case '1':
                    decodedString = "P1";
                    break;
                case '2':
                    decodedString = "P2";
                    break;
                case '3':
                    decodedString = "P3";
                    break;
                case '4':
                    decodedString = "C0";
                    break;
                case '5':
                    decodedString = "C1";
                    break;
                case '6':
                    decodedString = "C2";
                    break;
                case '7':
                    decodedString = "C3";
                    break;
                case '8':
                    decodedString = "B0";
                    break;
                case '9':
                    decodedString = "B1";
                    break;
                case 'A':
                    decodedString = "B2";
                    break;
                case 'B':
                    decodedString = "B3";
                    break;
                case 'C':
                    decodedString = "U0";
                    break;
                case 'D':
                    decodedString = "U1";
                    break;
                case 'E':
                    decodedString = "U2";
                    break;
                case 'F':
                    decodedString = "U3";
                    break;
                default:
                    break;
            }
            return decodedString;
        }
    }
}