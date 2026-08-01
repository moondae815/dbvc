using System;

namespace DBVC.Core
{
    /// <summary>
    /// Git Pull 중 병합 충돌이 감지되어 Pull을 안전하게 중단했음을 알린다. (설계 5절)
    /// </summary>
    public class MergeConflictException : Exception
    {
        public MergeConflictException(string message) : base(message)
        {
        }

        public MergeConflictException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
