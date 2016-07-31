using System;

namespace OctaveWrapper.Exceptions
{
    public class OctaveException : Exception
    {
        public OctaveException(string message, params object[] parameters)
            : base(String.Format(message, parameters))
        {
        }

        public OctaveException(Exception inner, string message, params object[] parameters)
            : base(String.Format(message, parameters), inner)
        {
        }
    }
}
