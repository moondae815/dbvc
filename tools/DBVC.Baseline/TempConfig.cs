namespace DBVC.Baseline
{
    /// <summary>
    /// 임시 매핑 파일의 자리.
    ///
    /// %APPDATA%\DBVC\mappings.json을 절대 쓰지 않는다. 거기에 운영 DB가 Mode = Write로 남으면
    /// 누군가 DBVC를 열고 초기화를 누르는 순간 운영에 DDL 트리거가 설치된다. 임시 파일을 쓰면
    /// 그런 상태가 한 번도 존재하지 않는다.
    /// </summary>
    public static class TempConfig
    {
        public static string CreateDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_baseline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static string MappingFilePath(string directory) => Path.Combine(directory, "mappings.json");
    }
}
