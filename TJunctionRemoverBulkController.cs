#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.ProBuilder;
using UnityEngine;
using UnityEngine.ProBuilder;
using EditorUtility = UnityEditor.EditorUtility;

public class TJunctionRemoverBulkController : MonoBehaviour
{
    [Header("Use AllEdgesEachVert search if >= Lenient.")]
    public TJunctionRemover.TJunctionToleranceOptionsEnum TJunctionToleranceOption = TJunctionRemover.TJunctionToleranceOptionsEnum.Lenient;
    public TJunctionRemover.TJunctionSearchModeEnum TJunctionSearchMode = TJunctionRemover.TJunctionSearchModeEnum.Greedy;
    public TJunctionRemover.TJunctionFixAlgorithmEnum TJunctionFixAlgorithm = TJunctionRemover.TJunctionFixAlgorithmEnum.SubdivideEdge;
    public int PassesEach = 1;
}

[CustomEditor(typeof(TJunctionRemoverBulkController))]
[CanEditMultipleObjects]
public class TJunctionRemoverBulkControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        TJunctionRemoverBulkController myScript = (TJunctionRemoverBulkController) target;

        if (GUILayout.Button("TJunction fix all children"))
        {
            var timeStarted = Time.realtimeSinceStartup;
            var go = myScript.gameObject;
            var children = go.GetComponentsInChildren<TJunctionRemover>();
            
            for (var i = 0; i < children.Length; i++)
            {
                var tJunctionRemover = children[i];
                var tJunctionRemoverGo = tJunctionRemover.gameObject;
                var currTJunctionToleranceOption = myScript.TJunctionToleranceOption;

                while ((int)currTJunctionToleranceOption >= 0)
                {
                    Debug.Log("TJunctionRemoverBulkControllerEditor: " + i + " / " + (children.Length - 1) + " " + tJunctionRemover.gameObject.name + " " + Enum.GetName(typeof(TJunctionRemover.TJunctionToleranceOptionsEnum), currTJunctionToleranceOption));
                    EditorUtility.ClearProgressBar();
                    var success = false;
                    try
                    {
                        tJunctionRemover.TJunctionToleranceOption = currTJunctionToleranceOption;
                        tJunctionRemover.TJunctionSearchMode = myScript.TJunctionSearchMode;
                        tJunctionRemover.Passes = myScript.PassesEach;
                        tJunctionRemover.OnValidate();
                        tJunctionRemover.BulkIAndLengthStatus = new SimpleTuple<int, int>(i, children.Length - 1);

                        tJunctionRemover.DoForSelf(true, true);

                        success = true;
                    }
                    catch (Exception e)
                    {
                        //suppress
                    }
                    // finally
                    // {
                    //     tJunctionRemover.BulkIAndLengthStatus = new SimpleTuple<int, int>(0, 0);
                    //     GC.Collect();
                    // }
                    
                    if (success) break;
                    else
                    {
                        tJunctionRemover = tJunctionRemover.BackupRestore();

                        // //fallback: switch algorithm before tolerance
                        // if (tJunctionRemover.TJunctionFixAlgorithm == TJunctionRemover.TJunctionFixAlgorithmEnum.SubdivideEdge)
                        //     tJunctionRemover.TJunctionFixAlgorithm = TJunctionRemover.TJunctionFixAlgorithmEnum.ConnectEdge;
                        // else
                        //     currTJunctionToleranceOption--;
                        currTJunctionToleranceOption--;
                    }
                }
                
                //pbm optimize
                var pbm = tJunctionRemoverGo.GetComponent<ProBuilderMesh>();
                pbm.Optimize(true);
            }

            Debug.Log("TJunctionRemoverBulkControllerEditor: DONE for " + children.Length + " in " + (Time.realtimeSinceStartup - timeStarted));
            EditorUtility.ClearProgressBar();
        }

        if (GUILayout.Button("Delete all backups"))
        {
            var go = myScript.gameObject;
            var children = go.GetComponentsInChildren<TJunctionRemover>();
            
            foreach (var child in children) child.BackupDelete();
        }
    }
}

#else
using UnityEngine;
public class TJunctionRemoverBulkController : MonoBehaviour
{
    private void Awake()
    {
        Destroy(this);
    }
}

#endif
