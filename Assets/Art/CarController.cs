using System;
using UnityEngine;

public class CarController : MonoBehaviour
{
    public Transform FrontRight;
    public Transform FrontLeft;
    public Transform BackRight;
    public Transform BackLeft;

    public float suspensionLength;
    public float suspensionStrength;

    public float gripAmmount;

    public float power;

    private float targetThrotte;
    private float currentThrottle;
    private float throttleVel;
    
    
    //private float steeringAngle;

    private Rigidbody m_RB;

    private float currentSteeringAngle;
    private float targetSteeringAngle;
    private float steeringAngleVel;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        m_RB = GetComponent<Rigidbody>();
    }

    // Update is called once per frame
    void FixedUpdate()
    {

        FrontLeft.localRotation = Quaternion.identity;
        FrontLeft.Rotate(transform.up, currentSteeringAngle);
        
        FrontRight.localRotation = Quaternion.identity;
        FrontRight.Rotate(transform.up, currentSteeringAngle);

        ApplySuspension(FrontRight, currentThrottle * power);
        ApplySuspension(FrontLeft, currentThrottle * power);
        ApplySuspension(BackRight, 0);
        ApplySuspension(BackLeft, 0);
    }

    private void Update()
    {
        /*
        DebugSuspension(FrontRight);
        DebugSuspension(FrontLeft);
        DebugSuspension(BackLeft);
        DebugSuspension(BackRight);
        */

        targetThrotte = 0;

        float maximumSteeringAngle = 30;


        if (Input.GetKey(KeyCode.W))
            targetThrotte += 1;

        if (Input.GetKey(KeyCode.S))
            targetThrotte -= 1;

        targetSteeringAngle = 0;
        if (Input.GetKey(KeyCode.A))
            targetSteeringAngle -= maximumSteeringAngle;

        if (Input.GetKey(KeyCode.D))
            targetSteeringAngle += maximumSteeringAngle;


        currentSteeringAngle = Mathf.SmoothDamp(currentSteeringAngle, targetSteeringAngle, ref steeringAngleVel, 0.4f);
        currentThrottle = Mathf.SmoothDamp(currentThrottle, targetThrotte, ref throttleVel, 0.4f);
    }

    private void DebugSuspension(Transform suspensionTransform)
    {
        RaycastHit hit;
        if (Physics.Raycast(suspensionTransform.position, -suspensionTransform.up, out hit, suspensionLength))
        {
            Vector3 roadForward = Vector3.ProjectOnPlane(suspensionTransform.forward, hit.normal).normalized;
            Vector3 roadNormal = hit.normal;
            Vector3 roadContact = hit.point;
            
            //Draw road base
            Debug.DrawLine(roadContact, roadContact + roadForward * 0.5f, Color.blue);
            Debug.DrawLine(roadContact, roadContact + roadNormal * 0.5f, Color.green);
            Debug.DrawLine(roadContact, roadContact + Vector3.Cross(roadNormal, roadForward) * 0.5f, Color.red);



            
            
            float compression = GetSpringCompression(suspensionTransform.position, hit.point);
            Debug.DrawLine(suspensionTransform.position, hit.point, Color.Lerp(Color.yellow, Color.red, compression));
            
            Matrix4x4 roadTransform = new Matrix4x4(Vector3.Cross(roadNormal, roadForward), roadNormal, roadForward, new Vector4(0,0,0,1));
            Vector3 slideVel = roadTransform.inverse * m_RB.GetPointVelocity(roadContact);
            Vector3 gripVel = new Vector3(-slideVel.x, 0, 0);
            Vector3 gripVelWS = (roadTransform * gripVel);
            
            Debug.DrawLine(suspensionTransform.position, suspensionTransform.position + gripVelWS, Color.cyan);

        }
        else
        {
            Debug.DrawLine(suspensionTransform.position, suspensionTransform.position - suspensionTransform.up * suspensionLength, Color.green);
        }
    }

    private float GetSpringCompression(Vector3 suspensionPos, Vector3 roadPos)
    {
        float compression = 1 - Vector3.Distance(suspensionPos, roadPos)/suspensionLength;
        compression *= compression;
        return compression;
    }

    private void ApplySuspension(Transform suspensionTransform, float drive)
    {
        RaycastHit hit;
        if (Physics.Raycast(suspensionTransform.position, -suspensionTransform.up, out hit, suspensionLength))
        {
            Vector3 roadForward = Vector3.ProjectOnPlane(suspensionTransform.forward, hit.normal).normalized;
            Vector3 roadNormal = hit.normal;
            Vector3 roadContact = hit.point;
            
            
            //Grip

            Matrix4x4 roadTransform = new Matrix4x4(Vector3.Cross(roadNormal, roadForward), roadNormal, roadForward, new Vector4(0,0,0,1));
            Vector3 slideVel = roadTransform.inverse * m_RB.GetPointVelocity(roadContact);
            Vector3 gripVel = new Vector3(-slideVel.x, 0, 0);
            Vector3 gripVelWS = (roadTransform * gripVel);

            Vector3 contactForce = Vector3.zero;
            contactForce += gripVelWS * gripAmmount;
            
            contactForce += Vector3.ProjectOnPlane(suspensionTransform.forward, hit.normal).normalized * drive;
            
            m_RB.AddForceAtPosition(contactForce, hit.point, ForceMode.Acceleration);


            //Suspension
            float distance = Vector3.Distance(suspensionTransform.position, hit.point);
            Transform wheel = suspensionTransform.GetChild(0);
            wheel.localPosition = new Vector3(wheel.localPosition.x, -distance + 0.3f, wheel.localPosition.z);
            
            
            float compression = GetSpringCompression(suspensionTransform.position, hit.point);
            float forceStrength = compression * suspensionStrength;
            
            m_RB.AddForceAtPosition(suspensionTransform.up * forceStrength, suspensionTransform.position);
            
            
            
            
            
            
        }
    }
    
}
