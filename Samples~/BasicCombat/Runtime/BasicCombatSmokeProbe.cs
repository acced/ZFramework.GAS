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
        private IEnumerator CheckBoltModes(TrainingArena arena){
            var targets=new[]{GameObject.Find("TrainingDummy"),GameObject.Find("ChainDummy1"),GameObject.Find("ChainDummy2")};
            var hits=new System.Collections.Generic.List<(CombatUnit u,float at)>();
            void Hit(CombatUnit u,float delta){if(delta<0)hits.Add((u,Time.time));}
            foreach(var go in targets)go.GetComponent<CombatUnit>().HealthChanged+=Hit;
            try{
                for(int mode=0;mode<2;mode++){
                    hits.Clear();if(mode==1)arena.ToggleBoltMode();
                    if(!arena.TryCast(1)){Debug.LogError("BasicCombat bolt cast failed, mode "+mode);yield break;}
                    double deadline=Time.realtimeSinceStartupAsDouble+16;
                    while(hits.Count<3 && Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                    bool valid=hits.Count==3 && hits[0].u.gameObject==targets[0] && hits[1].u!=hits[2].u &&
                        (mode==0?hits[2].at==hits[0].at:hits[1].at>hits[0].at && hits[2].at>hits[1].at);
                    if(!valid){Debug.LogError("BasicCombat bolt mode "+mode+" unexpected arrivals; count="+hits.Count);yield break;}
                    Debug.Log($"BasicCombat bolt {(mode==0?"fork":"bounce")}: {hits[0].at:F2}, {hits[1].at:F2}, {hits[2].at:F2}");
                    if(mode==0)yield return new WaitForSeconds(1.6f);
                }
            }finally{foreach(var go in targets)go.GetComponent<CombatUnit>().HealthChanged-=Hit;}
        }
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
                if(i==1){yield return CheckBoltModes(arena);if(_failed)break;continue;}
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
