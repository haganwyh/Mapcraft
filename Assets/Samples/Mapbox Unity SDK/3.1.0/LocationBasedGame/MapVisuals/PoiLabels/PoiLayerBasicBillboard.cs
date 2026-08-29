using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PoiLayerBasicBillboard : MonoBehaviour
{
    private Camera _camera;
    
    void Start()
    {
        _camera = Camera.main;    
    }

    void Update()
    {
        transform.LookAt(_camera.transform);
    }
}
