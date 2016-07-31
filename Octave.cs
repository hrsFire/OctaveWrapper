using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Microsoft.Win32;
using System.Threading;
using System.IO;
using OctaveWrapper.Exceptions;

namespace OctaveWrapper
{
    public class Octave
    {
        public const string OctaveTimeout = "Octave timeout";
        private const string OctaveAns = "ans =";

        public event OctaveRestartedEventHandler OctaveRestarted;
        public delegate void OctaveRestartedEventHandler(object sender, EventArgs e);

        private Process OctaveProcess;
        private string OctaveEchoString;
        private string PathToOctaveBinary;
        private bool CreateWindow;

        public Octave(string PathToOctaveBinary, bool CreateWindow, int timeout)
        {
            this.PathToOctaveBinary = PathToOctaveBinary;
            StartOctave(PathToOctaveBinary, CreateWindow, timeout);
        }

        private void StartOctave(string PathToOctaveBinary, bool CreateWindow, int timeout)
        {
            this.CreateWindow = CreateWindow;
            this.OctaveEchoString = Guid.NewGuid().ToString();
            OctaveProcess = new Process();

            // set process start info
            ProcessStartInfo pi = new ProcessStartInfo();
            pi.FileName = PathToOctaveBinary;
            pi.RedirectStandardInput = true;
            pi.RedirectStandardOutput = true;
            pi.RedirectStandardError = true;
            pi.UseShellExecute = false;
            pi.CreateNoWindow = !CreateWindow;
            pi.Verb = "open";

            pi.WorkingDirectory = ".";
            OctaveProcess.StartInfo = pi;

            try
            {
                OctaveProcess.Start();
            }
            catch (SystemException)
            {
                throw new OctaveException(new IOException("binary not found"), null);
            }

            OctaveProcess.OutputDataReceived += new DataReceivedEventHandler(OctaveProcess_OutputDataReceived);
            OctaveProcess.ErrorDataReceived += new DataReceivedEventHandler(OctaveProcess_OutputErrorReceived);
            OctaveProcess.BeginOutputReadLine();
            OctaveProcess.BeginErrorReadLine();
            string nullString = null;
            OctaveEntryText = ExecuteCommand(ref nullString, timeout);
        }

        public void StopOctave()
        {
            if (!OctaveProcess.HasExited)
            {
                OctaveProcess.OutputDataReceived -= new DataReceivedEventHandler(OctaveProcess_OutputDataReceived);
                OctaveProcess.ErrorDataReceived -= new DataReceivedEventHandler(OctaveProcess_OutputErrorReceived);
                OctaveProcess.Close();
            }
        }

        public bool HasExited()
        {
            return OctaveProcess.HasExited;
        }

        public bool GetBoolean(string varName, int timeout)
        {
            double res = GetScalar(varName, timeout);

            if(res <= 0)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        public double GetScalar(string varName, int timeout)
        {
            string res = ExecuteCommand(ref varName, timeout);
            string val = res.Substring(res.LastIndexOf("=") + 1).Trim().Replace(".", ",");

            return double.Parse(val);
        }

        public void GetColumnVector(string varName, int timeout, out double[] returnVector)
        {
            string res = ExecuteCommand(ref varName, timeout);
            string[] lines = res.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            List<double> data = new List<double>(new double[lines.Length]);

            if (data.Count > 0)
            {
                // the first element in "lines" is the variable name
                for (int m = 0; m < lines.Length; m++)
                {
                    string[] dataS = lines[m].Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    for (int k = 0; k < dataS.Length; k++)
                    {
                        data[m] = double.Parse(dataS[k].ToString().Replace(".", ","));
                    }
                }
            }

            returnVector = data.ToArray();
        }

        public void GetMatrix(string varName, int timeout, out double[][] returnMatrix)
        {
            string matrixParameter = varName + "(1,:)";
            string res = ExecuteCommand(ref matrixParameter, timeout); // get columns
            string[] lines = res.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            returnMatrix = new double[lines.Length][];

            for (int i = 0; i < returnMatrix.Length; i++)
            {
                GetColumnVector(varName + "(:, " + (i + 1) + ")", timeout, out returnMatrix[i]);
            }
        }

        StringBuilder SharedBuilder = new StringBuilder();
        ManualResetEvent OctaveDoneEvent = new ManualResetEvent(false);
        public string OctaveEntryText { get; internal set; }

        public void WorkThread(object o)
        {
            string command = (string)o;
            SharedBuilder.Clear();
            OctaveDoneEvent.Reset();

            if (command != null)
            {
                OctaveProcess.StandardInput.WriteLine(command);
            }

            OctaveProcess.StandardInput.WriteLine("\"" + OctaveEchoString + "\"");
            OctaveDoneEvent.WaitOne();
        }

        public string ExecuteCommand(ref string command, int timeout)
        {
            if (OctaveProcess.HasExited)
            {
                StartOctave(this.PathToOctaveBinary, this.CreateWindow, timeout);

                if (OctaveRestarted != null)
                    OctaveRestarted(this, EventArgs.Empty);
            }
            exitError = false;

            Thread thread = new Thread(new ParameterizedThreadStart(WorkThread));
            thread.Priority = ThreadPriority.Highest;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start(command);

            #if DEBUG

            System.DateTime beforeTime = DateTime.Now;

            #endif

            if(timeout >= 0)
            {
                if (!thread.Join(timeout))
                {
                    thread.Abort();
                    throw new OctaveException(OctaveTimeout);
                }
            }
            else
            {
                thread.Join();
            }
            if (exitError)
            {
                throw new OctaveException(SharedBuilder.ToString());
            }
            
            #if DEBUG

            System.Diagnostics.Debug.Write("Octave duration: " + DateTime.Now.Subtract(beforeTime).TotalSeconds + " s");

            #endif

            return SharedBuilder.ToString();
        }
            
        public string ExecuteCommandWithErrorCheck(ref string command, int timeout)
        {
            string temp = ExecuteCommand(ref command, timeout);
            if (temp.Contains("error"))
                return temp;
            else
                return null;
        }

        public Tuple<string, int> ExecuteCommands(string[] commands, int timeout)
        {
            if (commands == null || commands.Length == 0)
                return new Tuple<string, int>("No commands available", -1);

            string temp;

            for (int i = 0; i < commands.Length; i++)
            {
                temp = ExecuteCommandWithErrorCheck(ref commands[i], timeout);
                if (String.IsNullOrEmpty(temp))
                    return new Tuple<string, int>(temp, i);
            }

            return null;
        }

        public string ExecuteFile(ref string command, string filePath, int timeout)
        {
            if (OctaveProcess.HasExited)
            {
                StartOctave(this.PathToOctaveBinary, this.CreateWindow, timeout);

                if (OctaveRestarted != null)
                    OctaveRestarted(this, EventArgs.Empty);
            }
            exitError = false;

            Thread thread = new Thread(new ParameterizedThreadStart(WorkThread));
            thread.Priority = ThreadPriority.Highest;
            thread.SetApartmentState(ApartmentState.MTA);

            string newCommand = "load(" + filePath.Replace("\\", "\\\\") + "); " + command;
            thread.Start(newCommand);

            if (timeout >= 0)
            {
                if (!thread.Join(timeout))
                {
                    thread.Abort();
                    throw new OctaveException(OctaveTimeout);
                }
            }
            else
            {
                thread.Join();
            }
            if (exitError)
            {
                throw new OctaveException(SharedBuilder.ToString());
            }

            return SharedBuilder.ToString();
        }

        bool exitError = false;

        void OctaveProcess_OutputErrorReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            } else
            {
                SharedBuilder.Append(e.Data + "\r\n");
                OctaveDoneEvent.Set();
            }
        }

        void OctaveProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            if (e.Data.Trim() == OctaveAns + " " + OctaveEchoString)
                OctaveDoneEvent.Set();
            else
            {
                if (e.Data == OctaveAns)
                    return;
                else if (e.Data.Contains(OctaveAns))
                {
                    SharedBuilder.Append(e.Data.Replace(OctaveAns, ""));
                    return;
                }
                else if (String.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                SharedBuilder.Append(e.Data + "\r\n");
            }
        }

        public string SetBoolean(string varName, bool boolean, int timeout)
        {
            string parameter = varName + " = " + boolean + ";";
            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetScalar(string varName, double scalar, int timeout)
        {
            string parameter = varName + " = " + scalar.ToString().Replace(",", ".") + ";";
            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetScalar(string varName, string scalar, int timeout)
        {
            string parameter = varName + " = " + scalar.Replace(",", ".") + ";";
            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetColumnVector(string varName, ref double[] vector, int timeout)
        {
            string data = "";
            CreateVector(ref vector, true, out data);

            string parameter = varName + " = " + data + ";";
            data = null;

            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetColumnVector(string varName, ref string[] vector, int timeout)
        {
            string data;
            CreateVector(ref vector, true, out data);

            string parameter = varName + " = " + data + ";";
            data = null;

            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetRowVector(string varName, ref double[] vector, int timeout)
        {
            string data;
            CreateVector(ref vector, false, out data);

            string parameter = varName + " = " + data + ";";
            data = null;

            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetRowVector(string varName, ref string[] vector, int timeout)
        {
            string data;
            CreateVector(ref vector, false, out data);

            string parameter = varName + " = " + data + ";";
            data = null;

            return ExecuteCommand(ref parameter, timeout);
        }

        public string SetMatrix(string varName, ref double[][] matrix, int timeout)
        {
            StringBuilder command = new StringBuilder();
            command.Append(varName + " = [");
            string lineSign = ";";
            string columnSign = ",";

            for (int i = 0; i < matrix.Length; i++)
            {
                for (int n = 0; n < matrix[i].Length; n++)
                {
                    command.Append(matrix[i][n].ToString().Replace(",", "."));

                    if (n != (matrix[i].Length - 1))
                    {
                        command.Append(columnSign);
                    }
                }

                if (i != (matrix.Length - 1))
                {
                    command.Append(lineSign);
                }
            }

            command.Append("];");

            string stringCommand = command.ToString();
            return ExecuteCommand(ref stringCommand, timeout);
        }

        public string SetMatrix(string varName, ref string[][] matrix, int timeout)
        {
            StringBuilder command = new StringBuilder();
            command.Append(varName + " = [");
            string lineSign = ";";
            string columnSign = ",";

            for (int i = 0; i < matrix.Length; i++)
            {
                for (int n = 0; n < matrix[i].Length; n++)
                {
                    command.Append(matrix[i][n].Replace(",", "."));

                    if (n != (matrix[i].Length - 1))
                    {
                        command.Append(columnSign);
                    }
                }

                if (i != (matrix.Length - 1))
                {
                    command.Append(lineSign);
                }
            }

            command.Append("];");

            string stringCommand = command.ToString();
            return ExecuteCommand(ref stringCommand, timeout);
        }

        public string SetString(string varName, string stringValue, int timeout)
        {
            string parameter = varName + " = \"" + stringValue + "\";";
            return ExecuteCommand(ref parameter, timeout);
        }

        public void ClearAllVariables(int timeout)
        {
            string parameter = "clear";
            ExecuteCommand(ref parameter, timeout);
        }

        private void CreateVector(ref double[] vector, bool isColumnVector, out string data)
        {
            string sign;

            if (isColumnVector)
                sign = ";";
            else
                sign = ",";

            StringBuilder command = new StringBuilder();
            command.Append("[");

            for (int i = 0; i < vector.Length; i++)
            {
                command.Append(vector[i].ToString().Replace(",", "."));

                if (i != (vector.Length - 1))
                {
                    command.Append(sign);
                }
            }

            command.Append("]");

            data = command.ToString();
        }

        private void CreateVector(ref string[] vector, bool isColumnVector, out string data)
        {
            string sign;

            if (isColumnVector)
                sign = ";";
            else
                sign = ",";

            StringBuilder command = new StringBuilder();
            command.Append("[");

            for (int i = 0; i < vector.Length; i++)
            {
                if (vector[i] != null)
                {
                    command.Append(vector[i].Replace(",", "."));

                    if (i != (vector.Length - 1))
                    {
                        command.Append(sign);
                    }
                }
            }

            command.Append("]");

            data = command.ToString();
        }

        //https://www.gnu.org/software/octave/doc/interpreter/Predicates-for-Numeric-Objects.html#XREFisscalar
        //https://www.gnu.org/software/octave/doc/interpreter/Object-Sizes.html#Object-Sizes

        [Flags]
        public enum ResultTypes
        {
            None = 0x01,
            Scalar = 0x02,
            Vector = 0x04,
            Matrix = 0x08
        }

        public ResultTypes GetResultType(string variableName, int timeout)
        {
            string resultVariable = "own_matlab_octave_result_variable";

            string parameter = resultVariable + " = " + "isscalar(" + variableName + ")";
            ExecuteCommand(ref parameter, timeout);

            if(GetBoolean(resultVariable, timeout))
            {
                return ResultTypes.Scalar;
            }
            
            parameter = resultVariable + " = " + "ismatrix(" + variableName + ")";
            ExecuteCommand(ref parameter, timeout);
            
            if(GetBoolean(resultVariable, timeout))
            {

                parameter = resultVariable + " = " + "columns(" + variableName + ")";
                ExecuteCommand(ref parameter, timeout);

                int columns = (int) GetScalar(resultVariable, timeout);

                if(columns == 1)
                {
                    return ResultTypes.Vector;
                }
                else if(columns > 1)
                {
                    return ResultTypes.Matrix;
                }
            }

            return ResultTypes.None;
        }
    }
}
