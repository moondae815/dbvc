using System;

namespace DBVC.Core
{
    /// <summary>
    /// 받아올 변경과 겹치는 미커밋 변경 때문에 병합 체크아웃이 거부되어 Pull을 하지 못했음을 알린다.
    /// <see cref="MergeConflictException"/>과 달리 병합이 시작조차 되지 않았으므로
    /// 저장소는 손대지 않은 그대로이고 잃은 것이 없다.
    /// </summary>
    public class WorkingTreeConflictException : Exception
    {
        public WorkingTreeConflictException(string message) : base(message)
        {
        }

        public WorkingTreeConflictException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
