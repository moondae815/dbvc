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
        private RepositoryConnectRequest(RepositoryConnectKind kind, string? existingPath, string? remoteUrl, string? targetPath)
        {
            Kind = kind;
            ExistingPath = existingPath;
            RemoteUrl = remoteUrl;
            TargetPath = targetPath;
        }

        public static RepositoryConnectRequest ForExistingFolder(string path) =>
            new RepositoryConnectRequest(RepositoryConnectKind.ExistingFolder, path, null, null);

        public static RepositoryConnectRequest ForClone(string remoteUrl, string targetPath) =>
            new RepositoryConnectRequest(RepositoryConnectKind.Clone, null, remoteUrl, targetPath);

        public RepositoryConnectKind Kind { get; }

        /// <summary><see cref="RepositoryConnectKind.ExistingFolder"/>일 때만 값이 있다.</summary>
        public string? ExistingPath { get; }

        /// <summary><see cref="RepositoryConnectKind.Clone"/>일 때만 값이 있다.</summary>
        public string? RemoteUrl { get; }

        /// <summary><see cref="RepositoryConnectKind.Clone"/>일 때만 값이 있다. 아직 없는 폴더 경로다.</summary>
        public string? TargetPath { get; }
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
