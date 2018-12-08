﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GPUClothSimulator : MonoBehaviour {
    [Header("Simulation Parameters")]
    public float timestep = 0.02f;
    public int iterationNum = 5;
    public float vertexMass = 2;

    [Header("External Forces")]
    public Vector3 gravity;

    [Header("Cloth Parameters")]
    public int rows = 10;
    public int columns = 10;

    [Header("Distance Constraint")]
    public float distanceCompressionStiffness = 0.8f;
    public float distanceStretchStiffness = 0.8f;

    [Header("Bending Constraint")]
    public float bendingStiffness = 0.1f;
    public BendingMethod bendingMethod;

    [Header("Velocity Damping")]
    public DampingMethod dampingMethod;
    public float dampingStiffness = 0.02f;

    [Header("Point Constraints")]
    public PointConstraintType pointConstraintType;
    public int[] pointConstraintCustomIndices;

    [Header("Collision")]
    public GameObject[] collidableObjects;

    [Header("Compute Shader")]
    public ComputeShader PBDClothSolver;
    public int workGroupSize = 8; // TODO: figure out what the best value for this is

    // simulation data
    private float nextFrameTime = 0f;
    private Vector3[] positions;
    private Vector3[] velocities;
    private float[] frictions;
    private float invMass;
    private int numParticles;
    private int numDistanceConstraints;
    private int numCollidableSpheres, numCollidableCubes, numPointConstraints;
    // TODO: can we remove the following 3?
    private Vector3[] deltaPositionArray;    // The array that stores all the deltas
    private int[] deltaCounterArray;              // The array to initialize delta count buffer
    private UInt3Struct[] deltaPosUintArray;      // The array to initialize deltaposInt buffer

    // constraints
    private DistanceConstraintStruct[] distanceConstraints;
    private int[] pointConstraints;

    // compute buffers
    private ComputeBuffer positionsBuffer;
    private ComputeBuffer projectedPositionsBuffer;
    private ComputeBuffer velocitiesBuffer;
    private ComputeBuffer deltaPositionsBuffer;
    private ComputeBuffer deltaPositionsUIntBuffer;
    private ComputeBuffer deltaCounterBuffer;
    private ComputeBuffer distanceConstraintsBuffer;
    private ComputeBuffer collidableSpheresBuffer;
    private ComputeBuffer collidableCubesBuffer;
    private ComputeBuffer pointConstraintsBuffer;

    // kernel IDs
    private int applyExternalForcesKernel;
    private int dampVelocitiesKernel;
    private int applyExplicitEulerKernel;
    private int projectConstraintDeltasKernel;
    private int averageConstraintDeltasKernel;
    private int satisfyPointConstraintsKernel;
    private int satisfySphereCollisionsKernel;
    private int satisfyCubeCollisionsKernel;
    private int updatePositionsKernel;

    // num of work groups
    private int numGroups_Vertices;
    private int numGroups_DistanceConstraints;
    private int numGroups_PointConstraints;

    // mesh data
    private Mesh mesh;
    private Mesh reverseMesh;
    private Triangle[] triangles;



    private void Start () {
        // create new mesh
        mesh = Utility.CreateClothMesh(rows, columns);
        mesh.MarkDynamic();
        transform.GetComponent<MeshFilter>().mesh = mesh;

        numParticles = mesh.vertexCount;
        Vector3[] baseVertices = mesh.vertices;

        positions = new Vector3[numParticles];
        velocities = new Vector3[numParticles];
        frictions = new float[numParticles];
        deltaPositionArray = new Vector3[numParticles];
        deltaPosUintArray = new UInt3Struct[numParticles];
        deltaCounterArray = new int[numParticles];

        // create a new mesh for the opposite side
        CreateBackSide();

        // step 1-3: initialize position, velocity and weight
        for (int i = 0; i < numParticles; i++) {
            positions[i] = baseVertices[i];
            velocities[i] = Vector3.zero;
            frictions[i] = 1;

            // initialize delta Pos array
            deltaPositionArray[i] = Vector3.zero;

            // initialize delta Pos int array
            deltaPosUintArray[i].deltaXInt = 0;
            deltaPosUintArray[i].deltaYInt = 0;
            deltaPosUintArray[i].deltaZInt = 0;

            // initialize delta counter array
            deltaCounterArray[i] = 0;
        }
        invMass = 1.0f / vertexMass;

        // initialize triangles
        int[] triangleIndices = mesh.GetTriangles(0);
        triangles = new Triangle[triangleIndices.Length / 3];
        for (int i = 0; i < triangles.Length; i++) {
            triangles[i] = new Triangle(triangleIndices[i * 3], triangleIndices[i * 3 + 1], triangleIndices[i * 3 + 2]);
        }

        // modify positions to world coordinates before calculating constraint restlengths
        for (int i = 0; i < numParticles; i++) {
            positions[i] = transform.TransformPoint(positions[i]);
        }

        // add constraints
        AddDistanceConstraints();
        //AddBendingConstraints();

        // modify positions to world coordinates before calculating constraint restlengths
        for (int i = 0; i < numParticles; i++) {
            positions[i] = transform.InverseTransformPoint(positions[i]);
        }

        SetupComputeBuffers();
    }
  

    private void Update() {
        // TODO: do this on the GPU or figure out way to keep everything in local coor on the GPU (by setting external and collisions to local)
        // modify data to world coordinates
        for (int i = 0; i < numParticles; i++) {
            positions[i] = transform.TransformPoint(positions[i]);
            velocities[i] = transform.TransformVector(velocities[i]);
        }
        positionsBuffer.SetData(positions);
        velocitiesBuffer.SetData(velocities);
        PBDClothSolver.SetVector("gravity", gravity);
        PBDClothSolver.SetFloat("invMass", invMass);
        PBDClothSolver.SetFloat("stretchStiffness", distanceStretchStiffness);
        PBDClothSolver.SetFloat("compressionStiffness", distanceCompressionStiffness);

        // calculate the timestep 
        nextFrameTime += Time.deltaTime;
        int iter = 0;
        while (nextFrameTime > 0) {
            if (nextFrameTime < timestep) {
                break;
            }

            float dt = Mathf.Min(nextFrameTime, timestep);
            nextFrameTime -= dt;
            iter++;

            // send the dt data to the GPU
            PBDClothSolver.SetFloat("dt", dt);

            // step 5: apply external forces
            PBDClothSolver.Dispatch(applyExternalForcesKernel, numGroups_Vertices, 1, 1);

            // step 6: damp velocity
            if (dampingMethod != DampingMethod.noDamping) {
                PBDClothSolver.Dispatch(dampVelocitiesKernel, numGroups_Vertices, 1, 1);
            }

            // step 7: apply explicit Euler to positions based on velocity
            PBDClothSolver.Dispatch(applyExplicitEulerKernel, numGroups_Vertices, 1, 1);

            // step 8: clear current collisions and generate new collisions
            SetupCollisionComputeBuffers();

            // step 9-11: project constraints iterationNum times
            for (int j = 0; j < iterationNum; j++) {
                // distance constraints
                PBDClothSolver.Dispatch(projectConstraintDeltasKernel, numGroups_DistanceConstraints, 1, 1);
                PBDClothSolver.Dispatch(averageConstraintDeltasKernel, numGroups_Vertices, 1, 1);

                // collision constraints
                if (numCollidableSpheres > 0) {
                    PBDClothSolver.Dispatch(satisfySphereCollisionsKernel, numGroups_Vertices, 1, 1);
                }
                if (numCollidableCubes > 0) {
                    PBDClothSolver.Dispatch(satisfyCubeCollisionsKernel, numGroups_Vertices, 1, 1);
                }
            }

            // satisfy pointConstraints
            if (numPointConstraints > 0) {
                PBDClothSolver.Dispatch(satisfyPointConstraintsKernel, numGroups_PointConstraints, 1, 1);
            }

            // step 13 & 14: apply projected positions to actual vertices
            PBDClothSolver.Dispatch(updatePositionsKernel, numGroups_Vertices, 1, 1);

            // step 16: update all velocities using friction
            //ApplyFriction();
        }

        // get data from GPU back to CPU
        positionsBuffer.GetData(positions);
        velocitiesBuffer.GetData(velocities);

        // recalculate the center of the mesh
        Vector3 newCenter = Vector3.zero;
        Vector3 delta = Vector3.zero;
        if (pointConstraintType == PointConstraintType.none) {
            newCenter = GetComponentInChildren<Renderer>().bounds.center;
            delta = newCenter - transform.position;
        }

        // modify data to back to local coordinates
        for (int i = 0; i < numParticles; i++) {
            positions[i] = transform.InverseTransformPoint(positions[i] - delta);
            velocities[i] = transform.InverseTransformVector(velocities[i]);
        }

        if (pointConstraintType == PointConstraintType.none) transform.position = newCenter;

        // update everything into Unity
        mesh.vertices = positions;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        reverseMesh.vertices = positions;
        Vector3[] reverseNormals = mesh.normals;
        for (int i = 0; i < reverseNormals.Length; i++) {
            reverseNormals[i] *= -1;
        }
        reverseMesh.normals = reverseNormals;

        //print(Time.deltaTime + ", " + iter);
    }


    private void CreateBackSide() {
        GameObject newCloth = new GameObject("back");
        newCloth.transform.parent = transform;
        newCloth.transform.localPosition = Vector3.zero;
        newCloth.transform.localRotation = Quaternion.identity;
        newCloth.transform.localScale = new Vector3(1, 1, 1);
        newCloth.AddComponent<MeshRenderer>();
        newCloth.GetComponent<MeshRenderer>().material = GetComponent<MeshRenderer>().material;
        newCloth.AddComponent<MeshFilter>();
        reverseMesh = Utility.DeepCopyMesh(mesh);
        reverseMesh.MarkDynamic();

        // reverse the triangle order
        for (int m = 0; m < reverseMesh.subMeshCount; m++) {
            int[] triangles = reverseMesh.GetTriangles(m);
            for (int i = 0; i < triangles.Length; i += 3) {
                int temp = triangles[i + 0];
                triangles[i + 0] = triangles[i + 1];
                triangles[i + 1] = temp;
            }
            reverseMesh.SetTriangles(triangles, m);
        }
        newCloth.GetComponent<MeshFilter>().mesh = reverseMesh;
    }


    private void AddDistanceConstraints() {
        // use a set to get unique edges
        HashSet<Edge> edgeSet = new HashSet<Edge>(new EdgeComparer());
        for (int i = 0; i < triangles.Length; i++) {
            edgeSet.Add(new Edge(triangles[i].vertices[0], triangles[i].vertices[1]));
            edgeSet.Add(new Edge(triangles[i].vertices[0], triangles[i].vertices[2]));
            edgeSet.Add(new Edge(triangles[i].vertices[1], triangles[i].vertices[2]));
        };

        numDistanceConstraints = edgeSet.Count;
        distanceConstraints = new DistanceConstraintStruct[edgeSet.Count];
        int j = 0;
        foreach (Edge e in edgeSet) {
            EdgeStruct edge;
            edge.startIndex = e.startIndex;
            edge.endIndex = e.endIndex;
            distanceConstraints[j].edge = edge;
            distanceConstraints[j].restLength = (positions[edge.startIndex] - positions[edge.endIndex]).magnitude;
            j++;
        }
    }


    private void AddPointConstraints() {
        List<int> points = new List<int>();

        if (pointConstraintType == PointConstraintType.topCorners) {
            points.Add(rows * (columns + 1));
            points.Add((rows + 1) * (columns + 1) - 1);
        }
        else if (pointConstraintType == PointConstraintType.topRow) {
            for (int i = 0; i <= columns; i++) {
                points.Add(rows * (columns + 1) + i);
            }
        }
        else if (pointConstraintType == PointConstraintType.leftCorners) {
            points.Add(0);
            points.Add(rows * (columns + 1));
        }
        else if (pointConstraintType == PointConstraintType.leftRow) {
            for (int i = 0; i <= columns; i++) {
                points.Add(i * (columns + 1));
            }
        }

        for (int i = 0; i < pointConstraintCustomIndices.Length; i++) {
            int index = pointConstraintCustomIndices[i];
            if (index >= 0 && index < numParticles) {
                points.Add(index);
            }
        }

        numPointConstraints = points.Count;
        pointConstraints = new int[numPointConstraints];
        for (int i = 0; i < numPointConstraints; i++) {
            pointConstraints[i] = points[i];
        }
    }

    private void SetupComputeBuffers() {
        // create the compute buffers
        positionsBuffer = new ComputeBuffer(numParticles, sizeof(float) * 3);
        projectedPositionsBuffer = new ComputeBuffer(numParticles, sizeof(float) * 3);
        velocitiesBuffer = new ComputeBuffer(numParticles, sizeof(float) * 3);
        deltaPositionsBuffer = new ComputeBuffer(numParticles, sizeof(float) * 3);
        deltaPositionsUIntBuffer = new ComputeBuffer(numParticles, sizeof(uint) * 3);
        deltaCounterBuffer = new ComputeBuffer(numParticles, sizeof(int));
        distanceConstraintsBuffer = new ComputeBuffer(numDistanceConstraints, sizeof(float) + sizeof(int) * 2);

        // fill buffers with initial data
        positionsBuffer.SetData(positions);
        projectedPositionsBuffer.SetData(positions);
        velocitiesBuffer.SetData(velocities);
        deltaPositionsBuffer.SetData(deltaPositionArray);
        deltaPositionsUIntBuffer.SetData(deltaPosUintArray);
        deltaCounterBuffer.SetData(deltaCounterArray);
        distanceConstraintsBuffer.SetData(distanceConstraints);

        // identify the kernels
        applyExternalForcesKernel = PBDClothSolver.FindKernel("ApplyExternalForces");
        dampVelocitiesKernel = PBDClothSolver.FindKernel("DampVelocities");
        applyExplicitEulerKernel = PBDClothSolver.FindKernel("ApplyExplicitEuler");
        projectConstraintDeltasKernel = PBDClothSolver.FindKernel("ProjectConstraintDeltas");
        averageConstraintDeltasKernel = PBDClothSolver.FindKernel("AverageConstraintDeltas");
        updatePositionsKernel = PBDClothSolver.FindKernel("UpdatePositions");
        satisfySphereCollisionsKernel = PBDClothSolver.FindKernel("SatisfySphereCollisions");
        satisfyCubeCollisionsKernel = PBDClothSolver.FindKernel("SatisfyCubeCollisions");
        satisfyPointConstraintsKernel = PBDClothSolver.FindKernel("SatisfyPointConstraints");


        // set uniform data for kernels
        PBDClothSolver.SetInt("numParticles", numParticles);
        PBDClothSolver.SetInt("numDistanceConstraints", numDistanceConstraints);

        // bind buffer data to each kernel
        PBDClothSolver.SetBuffer(applyExternalForcesKernel, "velocities", velocitiesBuffer);

        PBDClothSolver.SetBuffer(dampVelocitiesKernel, "velocities", velocitiesBuffer);

        PBDClothSolver.SetBuffer(applyExplicitEulerKernel, "positions", positionsBuffer);
        PBDClothSolver.SetBuffer(applyExplicitEulerKernel, "projectedPositions", projectedPositionsBuffer);
        PBDClothSolver.SetBuffer(applyExplicitEulerKernel, "velocities", velocitiesBuffer);

        PBDClothSolver.SetBuffer(projectConstraintDeltasKernel, "projectedPositions", projectedPositionsBuffer);
        PBDClothSolver.SetBuffer(projectConstraintDeltasKernel, "deltaPos", deltaPositionsBuffer);
        PBDClothSolver.SetBuffer(projectConstraintDeltasKernel, "deltaPosAsInt", deltaPositionsUIntBuffer);
        PBDClothSolver.SetBuffer(projectConstraintDeltasKernel, "deltaCount", deltaCounterBuffer);
        PBDClothSolver.SetBuffer(projectConstraintDeltasKernel, "distanceConstraints", distanceConstraintsBuffer);

        PBDClothSolver.SetBuffer(averageConstraintDeltasKernel, "projectedPositions", projectedPositionsBuffer);
        PBDClothSolver.SetBuffer(averageConstraintDeltasKernel, "deltaPos", deltaPositionsBuffer);
        PBDClothSolver.SetBuffer(averageConstraintDeltasKernel, "deltaPosAsInt", deltaPositionsUIntBuffer);
        PBDClothSolver.SetBuffer(averageConstraintDeltasKernel, "deltaCount", deltaCounterBuffer);

        PBDClothSolver.SetBuffer(updatePositionsKernel, "positions", positionsBuffer);
        PBDClothSolver.SetBuffer(updatePositionsKernel, "projectedPositions", projectedPositionsBuffer);
        PBDClothSolver.SetBuffer(updatePositionsKernel, "velocities", velocitiesBuffer);

        //calculate and set the work group size
        numGroups_Vertices = Mathf.CeilToInt((float)numParticles / workGroupSize);
        numGroups_DistanceConstraints = Mathf.CeilToInt((float)numDistanceConstraints / workGroupSize);

    }


    private void SetupCollisionComputeBuffers() {
        List<GameObject> spheres = new List<GameObject>();
        List<GameObject> cubes = new List<GameObject>();

        // categorize all the collidable objects
        for (int j = 0; j < collidableObjects.Length; j++) {
            if (!collidableObjects[j].activeSelf) continue;

            Collider collider = collidableObjects[j].GetComponent<Collider>();

            if (collider.GetType() == typeof(SphereCollider)) {
                spheres.Add(collidableObjects[j]);
            }
            else if (collider.GetType() == typeof(BoxCollider)) {
                cubes.Add(collidableObjects[j]);
            }
        }

        // create the compute buffer for spheres
        CollidableSphereStruct[] collidableSpheres = new CollidableSphereStruct[spheres.Count];
        numCollidableSpheres = spheres.Count;
        for (int i = 0; i < numCollidableSpheres; i++) {
            collidableSpheres[i].center = spheres[i].transform.position + spheres[i].GetComponent<SphereCollider>().center;
            collidableSpheres[i].radius = spheres[i].transform.lossyScale.x * spheres[i].GetComponent<SphereCollider>().radius;
        }
        if (collidableSpheresBuffer != null) collidableSpheresBuffer.Release();

        if (numCollidableSpheres > 0) {
            collidableSpheresBuffer = new ComputeBuffer(numCollidableSpheres, sizeof(float) * 4);
            // fill buffers with initial data
            collidableSpheresBuffer.SetData(collidableSpheres);
            PBDClothSolver.SetInt("numCollidableSpheres", numCollidableSpheres);
            PBDClothSolver.SetBuffer(satisfySphereCollisionsKernel, "projectedPositions", projectedPositionsBuffer);
            PBDClothSolver.SetBuffer(satisfySphereCollisionsKernel, "collidableSpheres", collidableSpheresBuffer);
        }

        // create the compute buffer for cubes
        CollidableCubeStruct[] collidableCubes = new CollidableCubeStruct[cubes.Count];
        numCollidableCubes = cubes.Count;
        for (int i = 0; i < numCollidableCubes; i++) {
            collidableCubes[i].center = cubes[i].transform.position + cubes[i].GetComponent<BoxCollider>().center;
            float extent_x = cubes[i].transform.lossyScale.x * cubes[i].GetComponent<BoxCollider>().size.x / 2f;
            float extent_y = cubes[i].transform.lossyScale.y * cubes[i].GetComponent<BoxCollider>().size.y / 2f;
            float extent_z = cubes[i].transform.lossyScale.z * cubes[i].GetComponent<BoxCollider>().size.z / 2f;
            collidableCubes[i].extent = new Vector3(extent_x, extent_y, extent_z);
        }
        if (collidableCubesBuffer != null) collidableCubesBuffer.Release();

        if (numCollidableCubes > 0) {
            collidableCubesBuffer = new ComputeBuffer(numCollidableCubes, sizeof(float) * 6);
            // fill buffers with initial data
            collidableCubesBuffer.SetData(collidableCubes);
            PBDClothSolver.SetInt("numCollidableCubes", numCollidableCubes);
            PBDClothSolver.SetBuffer(satisfyCubeCollisionsKernel, "projectedPositions", projectedPositionsBuffer);
            PBDClothSolver.SetBuffer(satisfyCubeCollisionsKernel, "collidableCubes", collidableCubesBuffer);
        }

        // create compute buffer for point constraints
        AddPointConstraints();
        if (pointConstraintsBuffer != null) pointConstraintsBuffer.Release();

        if (numPointConstraints > 0) {
            pointConstraintsBuffer = new ComputeBuffer(numPointConstraints, sizeof(int));
            pointConstraintsBuffer.SetData(pointConstraints);
            PBDClothSolver.SetInt("numPointConstraints", numPointConstraints);
            PBDClothSolver.SetBuffer(satisfyPointConstraintsKernel, "positions", positionsBuffer);
            PBDClothSolver.SetBuffer(satisfyPointConstraintsKernel, "projectedPositions", projectedPositionsBuffer);
            PBDClothSolver.SetBuffer(satisfyPointConstraintsKernel, "pointConstraints", pointConstraintsBuffer);
            numGroups_PointConstraints = Mathf.CeilToInt((float)numPointConstraints / workGroupSize);
        }

    }


    private void OnDestroy() {
        if (positionsBuffer != null) positionsBuffer.Release();

        if (projectedPositionsBuffer != null) projectedPositionsBuffer.Release();

        if (velocitiesBuffer != null) velocitiesBuffer.Release();

        if (deltaPositionsBuffer != null) deltaPositionsBuffer.Release();

        if (deltaPositionsUIntBuffer != null) deltaPositionsUIntBuffer.Release();

        if (deltaCounterBuffer != null) deltaCounterBuffer.Release();

        if (distanceConstraintsBuffer != null) distanceConstraintsBuffer.Release();

        if (pointConstraintsBuffer != null) pointConstraintsBuffer.Release();

        if (collidableSpheresBuffer != null) collidableSpheresBuffer.Release();

        if (collidableCubesBuffer != null) collidableCubesBuffer.Release();

    }
}