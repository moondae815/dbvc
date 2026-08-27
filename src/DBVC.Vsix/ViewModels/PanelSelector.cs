using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    public enum DbvcPanelKind
    {
        ChangeList,
        SetupOverlay,
        DeploymentPanel
    }

    /// <summary>
    /// 본문 자리에 무엇을 띄울지. 순수 함수라 WPF 없이 테스트된다.
    ///
    /// mode를 먼저 본다. 운영·테스트 대상은 미초기화가 정상 상태이고, 거기서 초기화
    /// 오버레이가 뜨면 사용자가 누르는 버튼이 곧 금지된 DDL 트리거 설치다.
    /// </summary>
    public static class PanelSelector
    {
        public static DbvcPanelKind Select(MappingMode mode, bool isInitialized)
        {
            if (mode != MappingMode.Write) return DbvcPanelKind.DeploymentPanel;
            return isInitialized ? DbvcPanelKind.ChangeList : DbvcPanelKind.SetupOverlay;
        }
    }
}
