using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// mode가 허용하지 않는 동작을 불렀다.
    ///
    /// 조용한 false로 돌려보내지 않는 이유는, 버튼을 죽이는 것만으로는 나중에 코드 경로가
    /// 하나 늘 때 조용히 다시 열리기 때문이다. 메시지는 그대로 사용자에게 보인다.
    /// </summary>
    public class OperationNotAllowedException : Exception
    {
        public OperationNotAllowedException(MappingMode mode, DbvcOperation operation)
            : base(MappingPolicy.BuildDeniedMessage(mode, operation))
        {
            Mode = mode;
            Operation = operation;
        }

        public MappingMode Mode { get; }
        public DbvcOperation Operation { get; }
    }
}
