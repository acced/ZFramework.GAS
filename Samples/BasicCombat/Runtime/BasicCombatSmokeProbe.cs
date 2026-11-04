#if GAS_PLAYER_SMOKE && !UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;
namespace ZFramework.GAS.Samples.BasicCombat
{
    public sealed class BasicCombatSmokeProbe : MonoBehaviour
    {
        private bool _failed;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartProbe()=>new GameObject("GAS IL2CPP Smoke").AddComponent<BasicCombatSmokeProbe>();
        private void OnEnable()=>Application.logMessageReceived+=OnLog;
        private void OnDisable()=>Application.logMessageReceived-=OnLog;
        private void OnLog(string message,string stack,LogType type){if(type==LogType.Error || type==LogType.Exception || type==LogType.Assert)_failed=true;}
        private IEnumerator Start()
        {
            var arena=FindFirstObjectByType<TrainingArena>();if(arena==null || !arena.enabled){Application.Quit(1);yield break;}
            var knight=GameObject.Find("Knight");Transform chest=null;
            if(knight!=null)foreach(var t in knight.GetComponentsInChildren<Transform>())if(t.name=="Chest"){chest=t;break;}
            if(chest==null){Debug.LogError("BasicCombat smoke: Knight/Chest missing");Application.Quit(2);yield break;}
            yield return null;yield return null;
            var idle=chest.localRotation;
            for(int i=0;i<5;i++)
            {
                if(!arena.TryCast(i)){Debug.LogError("BasicCombat smoke: cast "+i+" failed");Application.Quit(2);yield break;}
                if(i==0){
                    float peak=0;double until=Time.realtimeSinceStartupAsDouble+3;
                    do{yield return null;peak=Mathf.Max(peak,Quaternion.Angle(idle,chest.localRotation));}while(Time.realtimeSinceStartupAsDouble<until);
                    float returned=Quaternion.Angle(idle,chest.localRotation);
                    Debug.Log($"BasicCombat slash chest: peak {peak:F1}°, after exit {returned:F1}° from idle");
                    if(peak<12 || returned>8){Debug.LogError("BasicCombat smoke: slash did not leave and return to the idle pose");Application.Quit(4);yield break;}
                }else yield return new WaitForSecondsRealtime(3);
            }
#if ENABLE_IL2CPP
            const string backend="IL2CPP";
#else
            const string backend="Mono";
#endif
            Debug.Log("GAS_"+backend+(_failed?"_SMOKE_FAILED":"_SMOKE_PASSED"));Application.Quit(_failed?3:0);
        }
    }
}
#endif
