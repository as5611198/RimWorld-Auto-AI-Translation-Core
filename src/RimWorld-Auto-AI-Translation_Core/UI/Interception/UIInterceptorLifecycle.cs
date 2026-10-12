using UnityEngine;
// 這個檔案負責 UI 攔截器生命週期管理。
// EN: This file manages the UI interceptor lifecycle.

namespace AutoTranslator_Core
{
    // 這個類別負責 UIInterceptorLifecycle 的主要流程與狀態。
    // EN: This class manages the main workflow and state for UIInterceptorLifecycle.
    public class UIInterceptorLifecycle : MonoBehaviour
    {
        // 這個方法負責處理 OnApplicationQuit 相關流程。
        // EN: This method handles on application quit.
        private void OnApplicationQuit()
        {
            TargetedHardcodedUi.HardcodedUiRuntimeObservationStore.Flush();
        }

        // 這個方法負責處理 OnDestroy 相關流程。
        // EN: This method handles on destroy.
        private void OnDestroy()
        {
            TargetedHardcodedUi.HardcodedUiRuntimeObservationStore.Flush();
        }

        private void Update()
        {
            TargetedHardcodedUi.HardcodedUiCaptureSession.Update();
        }
    }
}
