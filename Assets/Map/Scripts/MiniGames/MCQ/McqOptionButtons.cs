using System;
using UnityEngine;

public class McqOptionButtons : MonoBehaviour
{
    public static event Action<int> McqOptionSelected;

    public int buttonIndex;

    public void OptionSelected()
    {
        McqOptionSelected?.Invoke(buttonIndex);
    }
}
