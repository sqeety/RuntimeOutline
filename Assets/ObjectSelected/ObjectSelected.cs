using UnityEngine;

public class ObjectSelected : MonoBehaviour
{
    private void OnEnable()
    {
        ObjectSelectedManager.SelectedObject(gameObject, true);
    }

    private void OnDisable()
    {
        ObjectSelectedManager.SelectedObject(gameObject, false);
    }
}
