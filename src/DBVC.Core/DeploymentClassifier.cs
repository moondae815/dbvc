using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>차이 하나를 배포 스크립트에서 어떻게 다룰지.</summary>
    public enum ScriptDisposition
    {
        /// <summary>브랜치의 파일 내용을 그대로 담는다.</summary>
        Include,

        /// <summary>대상에 이미 있고 CREATE OR ALTER가 안 되는 타입이다. 사람이 ALTER를 쓴다.</summary>
        ExcludeManualChange,

        /// <summary>DB에만 있다. 담을 재료가 없다.</summary>
        ExcludeNotInBranch
    }

    /// <summary>
    /// 차이 검사 결과를 배포 스크립트의 분류로 옮긴다. 순수 함수이므로 DB도 파일도 없이
    /// 테스트된다. 이 판정이 곧 "대상 DB에 각 객체가 있는지 조회"를 대신한다 —
    /// 차이 검사가 이미 답을 들고 있어 다시 물을 필요가 없다.
    /// </summary>
    public static class DeploymentClassifier
    {
        public static ScriptDisposition Classify(ObjectDiffState state, string? objectType)
        {
            switch (state)
            {
                case ObjectDiffState.MissingInDatabase:
                    // 신규는 CREATE 그대로라 타입을 가리지 않는다.
                    return ScriptDisposition.Include;

                case ObjectDiffState.Modified:
                    return ObjectPathConvention.SupportsCreateOrAlter(objectType)
                        ? ScriptDisposition.Include
                        : ScriptDisposition.ExcludeManualChange;

                case ObjectDiffState.MissingInBranch:
                    return ScriptDisposition.ExcludeNotInBranch;

                default:
                    throw new InvalidOperationException($"처리되지 않은 {nameof(ObjectDiffState)}: {state}");
            }
        }
    }
}
