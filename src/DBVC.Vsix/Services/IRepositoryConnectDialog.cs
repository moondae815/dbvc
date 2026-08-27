using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>사용자가 고른 연결 방식.</summary>
    public enum RepositoryConnectKind
    {
        /// <summary>이미 받아둔 폴더를 그대로 쓴다.</summary>
        ExistingFolder = 0,

        /// <summary>원격에서 새로 받는다.</summary>
        Clone = 1
    }

    /// <summary>
    /// 저장소를 연결해 달라는 완성된 요청. 대화상자가 어떻게 생겼는지를 ViewModel에서 감춘다.
    /// </summary>
    public sealed class RepositoryConnectRequest
    {
        private RepositoryConnectRequest(
            RepositoryConnectKind kind, string? existingPath, string? remoteUrl, string? targetPath,
            MappingMode mode, string? branch)
        {
            Kind = kind;
            ExistingPath = existingPath;
            RemoteUrl = remoteUrl;
            TargetPath = targetPath;
            Mode = mode;
            Branch = branch;
        }

        public static RepositoryConnectRequest ForExistingFolder(string path, MappingMode mode, string? branch) =>
            new RepositoryConnectRequest(RepositoryConnectKind.ExistingFolder, path, null, null, mode, branch);

        public static RepositoryConnectRequest ForClone(string remoteUrl, string targetPath, MappingMode mode, string? branch) =>
            new RepositoryConnectRequest(RepositoryConnectKind.Clone, null, remoteUrl, targetPath, mode, branch);

        public RepositoryConnectKind Kind { get; }

        /// <summary><see cref="RepositoryConnectKind.ExistingFolder"/>일 때만 값이 있다.</summary>
        public string? ExistingPath { get; }

        /// <summary><see cref="RepositoryConnectKind.Clone"/>일 때만 값이 있다.</summary>
        public string? RemoteUrl { get; }

        /// <summary><see cref="RepositoryConnectKind.Clone"/>일 때만 값이 있다. 아직 없는 폴더 경로다.</summary>
        public string? TargetPath { get; }

        /// <summary>이 저장소의 용도. 허용 동작을 정한다.</summary>
        public MappingMode Mode { get; }

        /// <summary>
        /// 고정할 브랜치. 비면 전환이 자유롭다(개발 클론).
        /// 배포·감사에서는 대화상자가 비우지 못하게 막는다 - 고정 없는 배포 클론은
        /// 차단 판정이 막으려던 사고를 그대로 허용한다.
        /// </summary>
        public string? Branch { get; }
    }

    /// <summary>
    /// 저장소 연결 방식을 사용자에게 묻는다. ViewModel이 대화상자 구현에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IRepositoryConnectDialog
    {
        /// <summary>사용자가 취소하면 <c>null</c>.</summary>
        RepositoryConnectRequest? Prompt(string serverName, string databaseName);
    }
}
