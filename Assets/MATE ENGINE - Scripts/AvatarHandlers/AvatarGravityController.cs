using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using VRM;
using UniVRM10;
using UniGLTF.SpringBoneJobs.Blittables;
using System.Diagnostics;

public class AvatarGravityController : MonoBehaviour
{
    [Header("Impact Settings")]
    [Tooltip("How much motion from window drag affects SpringBones")]
    public float impactMultiplier = 0.05f;

    [Header("Debug")]
    public bool showDebugForce = true;
    public Color debugColor = Color.cyan;

    private Vector2Int previousWindowPos;
    private Vector3 currentForce;

    private List<VRMSpringBone> springBones = new();
    private Vrm10Instance vrm10Instance;
    private IntPtr unityHWND;

    // Test seam: replace the window position source
    private Func<Vector2Int> windowPositionProvider;

    void Start()
    {
        previousWindowPos = GetWindowPosition();
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;

        // VRM0 spring bones
        springBones.AddRange(GetComponentsInChildren<VRMSpringBone>(true));

        // VRM1 runtime handler
        vrm10Instance = GetComponentInParent<Vrm10Instance>();
    }

    void Update()
    {
        Vector2Int currentWindowPos = GetWindowPosition();
        Vector2Int delta = currentWindowPos - previousWindowPos;

        if (delta != Vector2Int.zero)
        {
            Vector3 impact = new Vector3(-delta.x, delta.y, 0).normalized * impactMultiplier;
            currentForce = impact;
        }
        else
        {
            currentForce = Vector3.zero;
        }

        // VRM0: set external force
        foreach (var spring in springBones)
        {
            if (spring != null)
                spring.ExternalForce = currentForce;
        }

        // VRM1: set model-level external force
        // Joint gravity is left as loaded from the .vrm so the avatar keeps its authored sway
        ApplyVrm10ModelLevel(new BlittableModelLevel { ExternalForce = currentForce });

        previousWindowPos = currentWindowPos;
    }

    private void ApplyVrm10ModelLevel(BlittableModelLevel level)
    {
        if (vrm10Instance == null) return;

        var spring = vrm10Instance.Runtime?.SpringBone;
        if (spring == null) return;

        spring.SetModelLevel(vrm10Instance.transform, level);
    }

    void OnDrawGizmos()
    {
        if (!showDebugForce) return;

        Gizmos.color = debugColor;
        Gizmos.DrawLine(transform.position, transform.position + currentForce);
        Gizmos.DrawSphere(transform.position + currentForce, 0.02f);
    }

    #region Windows API

    private Vector2Int GetWindowPosition()
    {
        if (windowPositionProvider != null)
            return windowPositionProvider();

        GetWindowRect(unityHWND, out RECT rect);
        return new Vector2Int(rect.left, rect.top);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    #endregion
}
