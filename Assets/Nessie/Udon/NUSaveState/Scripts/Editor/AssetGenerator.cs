﻿
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Nessie.Udon.Extensions;
using Nessie.Udon.SaveState.Data;

using ParameterType = UnityEngine.AnimatorControllerParameterType;

using StateTransition = UnityEditor.Animations.AnimatorStateTransition;

namespace Nessie.Udon.SaveState
{
    public static class AssetGenerator
    {
        public static readonly string PathNUSaveState = "Assets/Nessie/Udon/NUSaveState";

        public static readonly string PathWorld = $"{PathNUSaveState}/World";

        public static readonly string PathWorldAnimators = $"{PathWorld}/Animators";
        
        public static readonly string PathAvatar = $"{PathNUSaveState}/Avatar";
        
        public static readonly string PathAvatarAnimators = $"{PathAvatar}/Animators";
        private static readonly string PathAvatarExpressions = $"{PathAvatar}/Expressions";
        private static readonly string PathAvatarPackages = $"{PathAvatar}/Packages";
        private static readonly string PathAvatarPrefabs = $"{PathAvatar}/Prefabs";
        private static readonly string PathAvatarSOs = $"{PathAvatar}/SOs";
        
        private static readonly string[] MuscleNames = new string[]
        {
            "LeftHand.Index.",
            "LeftHand.Middle.",
            "LeftHand.Ring.",
            "LeftHand.Little.",
            "RightHand.Index.",
            "RightHand.Middle.",
            "RightHand.Ring.",
            "RightHand.Little.",
        };

        public static AnimatorController[] CreateWorldAnimators(AvatarData[] avatars, string folderPath)
        {
            AnimatorController[] controllers = new AnimatorController[avatars.Length];

            System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
            timer.Start();

            bool success = false;
            
            try
            {
                AssetDatabase.StartAssetEditing();
                
                Dictionary<string, AnimatorController> controllerDict = new Dictionary<string, AnimatorController>();

                foreach (AvatarData avatar in avatars)
                {
                    string parameterName = avatar.GetParameterName();
                    AnimatorController writer = avatar.ParameterWriter;
                    if (writer == null || writer.name != $"SaveState-{parameterName}")
                    {
                        continue;
                    }

                    if (!controllerDict.ContainsKey(parameterName))
                        controllerDict.Add(parameterName, writer);
                }

                for (int avatarIndex = 0; avatarIndex < avatars.Length; avatarIndex++)
                {
                    AvatarData avatar = avatars[avatarIndex];

                    string parameterName = avatar.GetParameterName();
                    if (!controllerDict.ContainsKey(parameterName))
                    {
                        AnimatorController controller = CreateWorldAnimator(avatar, folderPath);
                        controllerDict.Add(parameterName, controller);
                    }

                    controllers[avatarIndex] = controllerDict[parameterName];
                }

                success = true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
                
                timer.Stop();
                
                if (success)
                    DebugUtilities.Log($"World asset creation took: {timer.Elapsed:mm\\:ss\\.fff}");
            }

            return controllers;
        }

        public static AnimatorController CreateWorldAnimator(AvatarData avatar, string folderPath)
        {
            int avatarBytes = Mathf.CeilToInt(avatar.BitCount / 8f);
            ReadyPath(folderPath);

            string controllerPath = $"{folderPath}/SaveState-Write_{avatar.GetParameterName()}.controller";
            
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            
            string parameterName = avatar.GetParameterName();
            
            #region Parameters
            
            string[] velocityNames = new string[] { "VelocityX", "VelocityY", "VelocityZ" }; // Used when preparing the Parameters and Byte Layers.
            foreach (string velocityName in velocityNames)
                controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = velocityName, type = ParameterType.Float });
            
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "IgnoreTransition", type = ParameterType.Bool, defaultBool = true });
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "Batch", type = ParameterType.Int });

            for (int byteIndex = 0; byteIndex < avatarBytes; byteIndex++) // Prepare dummy parameters used to transfer the velocity parameters.
            {
                controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = $"b{byteIndex}", type = ParameterType.Float });
            }
            
            #endregion Parameters
            
            #region Clips

            AnimationClip[][] byteClips = new AnimationClip[avatarBytes][];
            for (int layerIndex = 0; layerIndex < byteClips.Length; layerIndex++)
            {
                byteClips[layerIndex] = new AnimationClip[8];

                for (int clipIndex = 0; clipIndex < 8; clipIndex++)
                {
                    float subtractionValue = 1f / Mathf.Pow(2, clipIndex + 1);

                    AnimationClip byteClip = new AnimationClip() { name = $"b{layerIndex}-{subtractionValue}".Replace(",", ".") };

                    byteClip.SetCurve("", typeof(Animator), $"b{layerIndex}", AnimationCurve.Linear(0, 0 - subtractionValue, 1, 1 - subtractionValue));

                    byteClips[layerIndex][clipIndex] = byteClip;
                    AssetDatabase.AddObjectToAsset(byteClip, controller);
                }
            }

            AnimationClip[] transferClips = new AnimationClip[avatarBytes];
            for (int byteIndex = 0; byteIndex < transferClips.Length; byteIndex++)
            {
                AnimationClip transferClip = new AnimationClip() { name = $"b{byteIndex}-transfer" };

                // Subtract the control bit (1/32th) and multiply by 32. Here's the max range for example: (1 - 0.03125) * 32 = 1 * 32 - 0.03125 * 32 = 32 - 1 = 31
                transferClip.SetCurve("", typeof(Animator), $"b{byteIndex}", byteIndex % 6 == 0 ? AnimationCurve.Linear(0, -1, 1, 31) : AnimationCurve.Linear(0, 0, 1, 32));

                transferClips[byteIndex] = transferClip;
                AssetDatabase.AddObjectToAsset(transferClip, controller);
            }
            
            AnimationClip[] identityClips = new AnimationClip[avatarBytes];
            for (int byteIndex = 0; byteIndex < identityClips.Length; byteIndex++)
            {
                AnimationClip identityClip = new AnimationClip() { name = $"b{byteIndex}-identity" };

                identityClip.SetCurve("", typeof(Animator), $"b{byteIndex}", AnimationCurve.Linear(0, 0, 1, 1)); // Create animations used to prevent animated floats from resetting when not animated.

                identityClips[byteIndex] = identityClip;
                AssetDatabase.AddObjectToAsset(identityClip, controller);
            }
            
            #endregion Clips

            #region Byte Layers
            
            for (int layerIndex = 0; layerIndex < avatarBytes; layerIndex++)
            {
                int parameterIndex = layerIndex;

                string layerName = $"byte {layerIndex}";
                AnimatorControllerLayer byteLayer = new AnimatorControllerLayer()
                {
                    name = layerName,
                    defaultWeight = 1,
                    stateMachine = new AnimatorStateMachine()
                    {
                        name = layerName,
                        hideFlags = HideFlags.HideInHierarchy,
                    }
                };
                SetMachineDefaultPositions(byteLayer.stateMachine);

                AssetDatabase.AddObjectToAsset(byteLayer.stateMachine, controller);
                controller.AddLayerNoUndo(byteLayer);

                AnimatorStateMachine byteMachine = byteLayer.stateMachine;

                AnimatorState transferState = byteMachine.AddStateNoUndo("Transfer", new Vector3(200, 0));
                transferState.writeDefaultValues = false;
                transferState.timeParameterActive = true;
                transferState.timeParameter = velocityNames[layerIndex % 3];
                transferState.motion = transferClips[parameterIndex];

                AnimatorState finalState = byteMachine.AddStateNoUndo("Finished", new Vector3(200, 1700));
                finalState.writeDefaultValues = false;
                
                AnimatorState[] byteStates = new AnimatorState[16];
                for (int stepIndex = 0; stepIndex < 8; stepIndex++)
                {
                    float bitDenominator = Mathf.Pow(2, stepIndex + 1);
                    
                    AnimatorState ignoreState = byteMachine.AddStateNoUndo($"Ignore {stepIndex}", new Vector3(300, 200 + stepIndex * 200));
                    ignoreState.writeDefaultValues = false;
                    ignoreState.timeParameterActive = true;
                    ignoreState.timeParameter = $"b{parameterIndex}";
                    ignoreState.motion = identityClips[parameterIndex];

                    AnimatorState writeState = byteMachine.AddStateNoUndo($"b{parameterIndex}-(1/{bitDenominator})", new Vector3(100, 200 + stepIndex * 200));
                    writeState.writeDefaultValues = false;
                    writeState.timeParameterActive = true;
                    writeState.timeParameter = $"b{parameterIndex}";
                    writeState.motion = byteClips[parameterIndex][stepIndex];

                    if (stepIndex > 0)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            StateTransition ignoreTransition = byteStates[(stepIndex - 1) * 2 + i].AddTransitionNoUndo(ignoreState);
                            SetNoTransitionTimes(ignoreTransition);
                            ignoreTransition.AddConditionNoUndo(AnimatorConditionMode.Less, 1 / bitDenominator, $"b{parameterIndex}");

                            StateTransition writeTransition = byteStates[(stepIndex - 1) * 2 + i].AddTransitionNoUndo(writeState);
                            SetNoTransitionTimes(writeTransition);
                            writeTransition.AddConditionNoUndo(AnimatorConditionMode.If, 0, "IgnoreTransition");
                        }
                    }
                    else
                    {
                        StateTransition ignoreTransition = transferState.AddTransitionNoUndo(ignoreState);
                        SetNoTransitionTimes(ignoreTransition);
                        ignoreTransition.AddConditionNoUndo(AnimatorConditionMode.Less, 1 / bitDenominator, $"b{parameterIndex}");
                        ignoreTransition.AddConditionNoUndo(AnimatorConditionMode.Equals, layerIndex / 3 + 1, "Batch");

                        StateTransition writeTransition = transferState.AddTransitionNoUndo(writeState);
                        SetNoTransitionTimes(writeTransition);
                        writeTransition.AddConditionNoUndo(AnimatorConditionMode.Equals, layerIndex / 3 + 1, "Batch");
                    }

                    VRCAvatarParameterDriver byteDriver = writeState.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                    
                    byte debugByte = (byte)(1 << (7 - stepIndex));
                    byteDriver.debugString = $"[NUSS] b{layerIndex} += {Convert.ToString(debugByte, 2).PadLeft(8, '0')}";

                    byteDriver.parameters = new List<VRC_AvatarParameterDriver.Parameter>()
                    {
                        new VRC_AvatarParameterDriver.Parameter()
                        {
                            type = VRC_AvatarParameterDriver.ChangeType.Add,
                            name = $"{parameterName}_{layerIndex / 2}",
                            value = 1 / Mathf.Pow(2, stepIndex + (layerIndex & 1 ^ 1) * 8 + 1),
                        }
                    };
                    
                    byteStates[stepIndex * 2 + 1] = ignoreState;
                    byteStates[stepIndex * 2] = writeState;
                }

                StateTransition finalTransitionL = byteStates[14].AddTransitionNoUndo(finalState);
                SetNoTransitionTimes(finalTransitionL);
                finalTransitionL.AddConditionNoUndo(AnimatorConditionMode.If, 0, "IgnoreTransition");

                StateTransition finalTransitionR = byteStates[15].AddTransitionNoUndo(finalState);
                SetNoTransitionTimes(finalTransitionR);
                finalTransitionR.AddConditionNoUndo(AnimatorConditionMode.If, 0, "IgnoreTransition");
            }
            
            #endregion Byte Layers
            
            AssetDatabase.ImportAsset(controllerPath);
            
            return controller;
        }

        public static AnimatorController CreateAvatarAnimator(AvatarData avatar, string folderPath)
        {
            ReadyPath(folderPath);

            // Prepare animator controller.
            string controllerPath = $"{folderPath}/SaveState-{avatar.name}_Controller.controller";

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            AnimatorStateMachine newStateMachine = controller.layers[0].stateMachine;
            SetMachineDefaultPositions(newStateMachine);

            // Prepare default animation.
            AnimationClip newDefaultClip = new AnimationClip() { name = "Default" };
            newDefaultClip.SetCurve("", typeof(Animator), "RootT.y", AnimationCurve.Constant(0, 0, 1));

            AssetDatabase.AddObjectToAsset(newDefaultClip, controller);

            // Prepare default state an animation.
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "IsLocal", type = ParameterType.Bool, defaultBool = false });
            AnimatorState newDefaultState = newStateMachine.AddStateNoUndo("Default", new Vector3(200, 0));
            newDefaultState.motion = newDefaultClip;

            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "Base", type = ParameterType.Float, defaultFloat = 1 });

            // Prepare base BlendTree animation.
            AnimationClip newBaseClip = new AnimationClip() { name = "SaveState-Base" };

            newBaseClip.SetCurve("", typeof(Animator), "RootT.y", AnimationCurve.Constant(0, 0, 1));
            newBaseClip.SetCurve("SaveState-Avatar/hips/SaveState-Key", typeof(GameObject), "m_IsActive", AnimationCurve.Constant(0, 0, 1));

            Vector3 keyCoordinate = avatar.GetKeyCoordinate() / 100f * 50f; // Account for the scale of the armature and bounds.

            newBaseClip.SetCurve("SaveState-Avatar/hips/SaveState-Key", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Constant(0, 0, keyCoordinate.x));
            newBaseClip.SetCurve("SaveState-Avatar/hips/SaveState-Key", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Constant(0, 0, keyCoordinate.y));
            newBaseClip.SetCurve("SaveState-Avatar/hips/SaveState-Key", typeof(Transform), "m_LocalPosition.z", AnimationCurve.Constant(0, 0, keyCoordinate.z));
            for (int i = 0; i < MuscleNames.Length; i++)
            {
                newBaseClip.SetCurve("", typeof(Animator), $"{MuscleNames[i]}2 Stretched", AnimationCurve.Constant(0, 0, 0.81002f));
                newBaseClip.SetCurve("", typeof(Animator), $"{MuscleNames[i]}3 Stretched", AnimationCurve.Constant(0, 0, 0.81002f));
            }

            AssetDatabase.AddObjectToAsset(newBaseClip, controller);

            string parameterName = avatar.GetParameterName();

            int numPages = Mathf.CeilToInt(avatar.BitCount / (float)AvatarData.BITS_PER_PAGE);//number of pages needed

            AnimatorState blendBase = null;

            for (int page = -1; page < numPages; page++)
            {

                // Prepare data BlendTree state.
                AnimatorState newBlendState = controller.CreateBlendTreeInController("Data Blend", out BlendTree newTree, 0);
                if (page == -1) blendBase = newBlendState;
                ChildAnimatorState[] newChildStates = newStateMachine.states;
                newChildStates[page+2].position = new Vector2(200, 50*(page+2));
                newStateMachine.states = newChildStates;


                StateTransition newBlendTransition;
                if (page == -1)
                {
                    newBlendTransition = newDefaultState.AddTransitionNoUndo(newBlendState);
                }
                else
                {
                    newBlendTransition = newStateMachine.AddAnyStateTransitionNoUndo(newBlendState);
                }
                newBlendTransition.exitTime = 1;
                newBlendTransition.duration = 0;
                newBlendTransition.AddConditionNoUndo(AnimatorConditionMode.If, 1, "IsLocal");
                if (page > -1)
                {
                    float upperVelocity = -page / 256f / 32;
                    float lowerVelocity = -(page + 1) / 256f / 32;
                    newBlendTransition.AddConditionNoUndo(AnimatorConditionMode.Greater, lowerVelocity, "VelocityY");
                    newBlendTransition.AddConditionNoUndo(AnimatorConditionMode.Less, upperVelocity, "VelocityY");
                }

                newTree.blendType = BlendTreeType.Direct;

                // Prepare VRC Behaviours.
                VRCPlayableLayerControl layerControl = newBlendState.AddStateMachineBehaviour<VRCPlayableLayerControl>();
                VRCAnimatorTrackingControl trackingControl = newBlendState.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();

                layerControl.goalWeight = 1;

                trackingControl.trackingLeftFingers = VRC_AnimatorTrackingControl.TrackingType.Animation;
                trackingControl.trackingRightFingers = VRC_AnimatorTrackingControl.TrackingType.Animation;

                newTree.AddChildNoUndo(newBaseClip);

                if (page != -1)
                {
                    // Prepare data BlendTree animations.
                    int pageBitCount = Mathf.Min(AvatarData.BITS_PER_PAGE, (avatar.BitCount - page * AvatarData.BITS_PER_PAGE));
                    int byteCount = Mathf.CeilToInt(pageBitCount / 16f);
                    for (int byteIndex = 0; byteIndex < byteCount; byteIndex++)
                    {
                        AnimationClip newClip = new AnimationClip() { name = $"SaveState-{parameterName}_{byteIndex + page * AvatarData.BITS_PER_PAGE / 16}.anim" };

                        newClip.SetCurve("", typeof(Animator), $"{MuscleNames[byteIndex % MuscleNames.Length]}{3 - byteIndex / MuscleNames.Length} Stretched", AnimationCurve.Constant(0, 0, 1));
                        newTree.AddChildNoUndo(newClip);

                        AssetDatabase.AddObjectToAsset(newClip, controller);
                    }
                }

                // Prepare BlendTree parameters.
                ChildMotion[] newChildren = newTree.children;

                newChildren[0].directBlendParameter = "Base";
                if (page != -1)
                {
                    for (int childIndex = 1; childIndex < newChildren.Length; childIndex++)
                    {
                        string newParameter = $"{parameterName}_{childIndex - 1 + page * AvatarData.BITS_PER_PAGE / 16}";

                        controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = newParameter, type = ParameterType.Float });
                        newChildren[childIndex].directBlendParameter = newParameter;
                    }
                }

                newTree.children = newChildren;
            }

            AddFlowControlLayer(avatar, controller, parameterName);
            
            AssetDatabase.ImportAsset(controllerPath);
            
            return controller;
        }

        private static void AddFlowControlLayer(AvatarData avatar, AnimatorController controller, string parameterName)
        {
            string layerName = "Flow Control";
            AnimatorStateMachine batchMachine = new AnimatorStateMachine()
            {
                name = layerName,
                hideFlags = HideFlags.HideInHierarchy,
            };
            SetMachineDefaultPositions(batchMachine);
            AssetDatabase.AddObjectToAsset(batchMachine, controller);
            
            AnimatorControllerLayer flowLayer = new AnimatorControllerLayer()
            {
                name = layerName,
                defaultWeight = 1f,
                stateMachine = batchMachine,
            };
            controller.AddLayerNoUndo(flowLayer);
            
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "InStation", type = ParameterType.Bool });
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "Seated", type = ParameterType.Bool });
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "Batch", type = ParameterType.Int });
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "VelocityX", type = ParameterType.Float });
            controller.AddParameterNoUndo(new AnimatorControllerParameter() { name = "VelocityY", type = ParameterType.Float });

            //This was hard-coded to 12, not sure what this actually means, BUT it seems to alternate between 1 or 2 parameters each, with an empty first node; I can work with that
            //oh and it seems to be 2 bytes per parameter(?)
            AnimatorState[] batchStates = new AnimatorState[Mathf.CeilToInt((avatar.BitCount/16)/1.5f+1)];//JANK ALERT PLEASE LOOK AWAY

            // Empty default state to avoid having the animator controller get stuck.
            batchStates[0] = batchMachine.AddStateNoUndo("Default", new Vector3(200, 0));

            int driverParameterIndex = 0;
            for (int stateIndex = 1; stateIndex < batchStates.Length; stateIndex++)
            {
                batchStates[stateIndex] = batchMachine.AddStateNoUndo($"Batch {stateIndex}", new Vector3(200, 100 * stateIndex));

                StateTransition batchTransition = batchStates[stateIndex - 1].AddTransitionNoUndo(batchStates[stateIndex]);
                SetNoTransitionTimes(batchTransition);

                if (stateIndex == 1)
                {
                    batchTransition.AddConditionNoUndo(AnimatorConditionMode.If, 0f, "InStation");
                    batchTransition.AddConditionNoUndo(AnimatorConditionMode.IfNot, 0f, "Seated");
                }
                batchTransition.AddConditionNoUndo(stateIndex % 2 == 0 ? AnimatorConditionMode.Less : AnimatorConditionMode.Greater, 0.03125f, "VelocityX");
                
                VRCAvatarParameterDriver batchDriver = batchStates[stateIndex].AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                // batchDriver.debugString = $"[NUSS] Batch: {stateIndex}";

                List<VRC_AvatarParameterDriver.Parameter> batchParameters = new List<VRC_AvatarParameterDriver.Parameter>
                {
                    new VRC_AvatarParameterDriver.Parameter()
                    {
                        type = VRC_AvatarParameterDriver.ChangeType.Set,
                        name = "Batch",
                        value = stateIndex,
                    },
                };
                
                for (int i = 0; i < 1 + (stateIndex % 2); i++)
                {
                    batchParameters.Add(new VRC_AvatarParameterDriver.Parameter()
                    {
                        type = VRC_AvatarParameterDriver.ChangeType.Set,
                        name = $"{parameterName}_{driverParameterIndex++}",
                    });
                }

                batchDriver.parameters = batchParameters;
            }
        }

        private static void SetMachineDefaultPositions(AnimatorStateMachine machine)
        {
            machine.entryPosition = new Vector2(-30, 0);
            machine.anyStatePosition = new Vector2(-30, 50);
            machine.exitPosition = new Vector2(-30, 100);
        }
        
        private static void SetNoTransitionTimes(StateTransition transition)
        {
            transition.duration = 0f;
            transition.exitTime = 0f;
            transition.hasExitTime = false;
        }
        
        public static VRCExpressionsMenu CreateAvatarMenu(AvatarData avatar, string folderPath)
        {
            ReadyPath(folderPath);

            VRCExpressionsMenu menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls.Add(new VRCExpressionsMenu.Control()
            {
                name = "<voffset=15em><size=110%><color=#00FF9F><b>NUSaveState"
            });
            
            string menuPath = $"{folderPath}/SaveState-{avatar.name}_Menu.asset";
            AssetDatabase.CreateAsset(menu, menuPath);

            AssetDatabase.ImportAsset(menuPath);
            
            return menu;
        }
        
        public static VRCExpressionParameters CreateAvatarParameters(AvatarData avatar, string folderPath)
        {
            ReadyPath(folderPath);

            VRCExpressionParameters parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();

            string parameterName = avatar.GetParameterName();
            int paramCount = Mathf.CeilToInt(avatar.BitCount / 16f);

            VRCExpressionParameters.Parameter[] expressionControls = new VRCExpressionParameters.Parameter[paramCount];
            for (int i = 0; i < expressionControls.Length; i++)
            {
                expressionControls[i] = new VRCExpressionParameters.Parameter()
                {
                    name = $"{parameterName}_{i}",
                    valueType = VRCExpressionParameters.ValueType.Float,
                    networkSynced = false
                };
            }

            parameters.parameters = expressionControls;

            string parametersPath = $"{folderPath}/SaveState-{avatar.name}_Parameters.asset";
            AssetDatabase.CreateAsset(parameters, parametersPath);

            AssetDatabase.ImportAsset(parametersPath);
            
            return parameters;
        }
        
        public static string CreateAvatarPrefab(AvatarData avatar, AnimatorController controller, VRCExpressionsMenu menu, VRCExpressionParameters parameters, string folderPath)
        {
            ReadyPath(folderPath);
            
            string prefabPath = $"{folderPath}/SaveState-Avatar_{avatar.name}.prefab";
            
            GameObject templatePrefab = PrefabUtility.LoadPrefabContents($"{PathAvatar}/Template/SaveState-Avatar-Template.prefab");
            
            VRCAvatarDescriptor newAvatarDescriptor = templatePrefab.GetComponent<VRCAvatarDescriptor>();
            newAvatarDescriptor.expressionsMenu = menu;
            newAvatarDescriptor.expressionParameters = parameters;
            
            VRCAvatarDescriptor.CustomAnimLayer[] baseLayers = newAvatarDescriptor.baseAnimationLayers;
            baseLayers[3].animatorController = controller;
            baseLayers[4].animatorController = controller;
            
            VRCAvatarDescriptor.CustomAnimLayer[] specialLayers = newAvatarDescriptor.specialAnimationLayers;
            specialLayers[1].animatorController = controller;
            
            PrefabUtility.SaveAsPrefabAsset(templatePrefab, prefabPath);
            
            PrefabUtility.UnloadPrefabContents(templatePrefab);

            // TODO: Find a way to load prefab asset inside of AssetEditing after saving using SaveAsPrefabAsset.
            //return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return prefabPath;
        }
        
        public static string[] CreateAvatarPackages(AvatarData[] avatars, string folderPath)
        {
            string[] paths = new string[avatars.Length];
            for (int i = 0; i < avatars.Length; i++)
            {
                paths[i] = CreateAvatarPackage(avatars[i], folderPath);
            }

            return paths;
        }
        
        public static string CreateAvatarPackage(AvatarData avatar, string folderPath)
        {
            ReadyPath(folderPath);
            
            AnimatorController controller = CreateAvatarAnimator(avatar, $"{PathAvatar}/Animators");
            VRCExpressionsMenu menu = CreateAvatarMenu(avatar, $"{PathAvatar}/Expressions");
            VRCExpressionParameters parameters = CreateAvatarParameters(avatar, $"{PathAvatar}/Expressions");
            
            string[] assetPaths = new string[]
            {
                AssetDatabase.GetAssetPath(controller),
                AssetDatabase.GetAssetPath(menu),
                AssetDatabase.GetAssetPath(parameters),
                CreateAvatarPrefab(avatar, controller, menu, parameters, $"{PathAvatar}/Prefabs"),
                    
                PathAvatar + "/Materials/Surface.mat",
                PathAvatar + "/Template/SaveState-Avatar.fbx",
            };
            
            // TODO: Get dependencies instead of being hard-coded.
            //List<string> packageAssetPaths = new List<string>();
            //foreach (string dependencyPath in AssetDatabase.GetDependencies(assetPaths, true))
            //{
            //    Debug.Log("dependency: " + dependencyPath + " " + dependencyPath.StartsWith(AssetGenerator.PathNUSaveState));
            //    if (!dependencyPath.StartsWith(AssetGenerator.PathNUSaveState))
            //        continue;
            //        
            //    packageAssetPaths.Add(dependencyPath);
            //}

            string pathUnityPackage = $"{folderPath}/SaveState-{avatar.name}_Package.unitypackage";
            AssetDatabase.ExportPackage(assetPaths, pathUnityPackage, ExportPackageOptions.Default);
            AssetDatabase.ImportAsset(pathUnityPackage);

            return pathUnityPackage;
        }
        
        public static string[] MigrateSaveStateData(NUSaveState saveState, NUSaveStateData data, string folderPath) // Does not account for overlapping data. E.g. Float split between two avatars.
        {
            if (!(data.Instructions?.Length > 0))
            {
                return null;
            }

            ReadyPath(folderPath);

            Legacy.Preferences preferences = data.Preferences;
            SerializedObject so = new SerializedObject(saveState);
            SerializedProperty propertyAvatarIDs = so.FindProperty("dataAvatarIDs");
            SerializedProperty propAvatarCoordinates = so.FindProperty("dataKeyCoords");

            bool isLegacyParameter = preferences.Parameter != AvatarData.DEFAULT_PARAMETER_NAME;

            Legacy.Instruction[][] avatarInstructions = SplitAvatarInstructions(data.Instructions);
            string[] avatarDataPaths = new string[avatarInstructions.Length];
            for (int avatarIndex = 0; avatarIndex < avatarInstructions.Length; avatarIndex++)
            {
                AvatarData avatarData = ScriptableObject.CreateInstance<AvatarData>();
                
                avatarData.AvatarBlueprint = avatarIndex < propertyAvatarIDs.arraySize ? propertyAvatarIDs.GetArrayElementAtIndex(avatarIndex).stringValue : "";
                
                bool isLegacyCoordinate = avatarIndex > 0; // First avatar was always getting the initial random values from the key, the rest depended on previous ones.
                avatarData.IsLegacy = isLegacyParameter || isLegacyCoordinate;
                if (isLegacyParameter)
                {
                    avatarData.ParameterName = preferences.Parameter;
                }

                if (!isLegacyCoordinate)
                {
                    avatarData.EncryptionKey = preferences.Seed;
                }

                if (avatarData.IsLegacy && avatarIndex < propAvatarCoordinates.arraySize)
                {
                    avatarData.KeyCoordinate = propAvatarCoordinates.GetArrayElementAtIndex(avatarIndex).vector3Value / 50f;
                }
                
                avatarData.VariableSlots = InstructionsToVariableSlots(avatarInstructions[avatarIndex]);
                avatarData.BitCount = avatarData.VariableSlots.GetBitSum();
                
                string newAvatarDataPath = AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/Avatar_Data.asset");
                avatarDataPaths[avatarIndex] = newAvatarDataPath;
                AssetDatabase.CreateAsset(avatarData, newAvatarDataPath);
            }

            return avatarDataPaths;
        }

        public static VariableSlot[] InstructionsToVariableSlots(Legacy.Instruction[] instructions)
        {
            VariableSlot[] slots = new VariableSlot[instructions.Length];
            for (int i = 0; i < instructions.Length; i++)
            {
                NUExtensions.Variable variable = instructions[i].Variable;
                slots[i] = new VariableSlot(variable.Name, variable.Type);
            }

            return slots;
        }
        
        /// <summary>
        /// Splits instructions into groups where their bit sum is 256 or less and ignores overlapping instructions.
        /// </summary>
        /// <param name="instructions"></param>
        /// <returns></returns>
        public static Legacy.Instruction[][] SplitAvatarInstructions(Legacy.Instruction[] instructions)
        {
            instructions = ReorderInstructions(instructions);

            List<List<Legacy.Instruction>> instructionLists = new List<List<Legacy.Instruction>>();

            int currentBits = 0;
            int avatarIndex = 0;
            for (int i = 0; i < instructions.Length; i++)
            {
                if (currentBits == 0)
                {
                    instructionLists.Add(new List<Legacy.Instruction>());
                }
                
                Legacy.Instruction instruction = instructions[i];
                currentBits += instruction.BitCount;
                
                if (currentBits > 256)
                {
                    currentBits = 0;
                    NUExtensions.Variable variable = instruction.Variable;
                    Debug.LogWarning($"Found overlapping variable {variable.Name} ({variable.Type}). This will prevent backwards compatibility.\nPlease edit the Avatar Data asset to determine which avatar it should be stored on.");
                    continue;
                }
                
                instructionLists[avatarIndex].Add(instruction);
                
                if (currentBits == 256)
                {
                    currentBits = 0;
                    avatarIndex++;
                }
            }

            return instructionLists.Select(l => l.ToArray()).ToArray();
        }
        
        /// <summary>
        /// Puts boolean instructions at the end of the instruction stack to reflect how they were stored using the legacy system.
        /// </summary>
        /// <param name="instructions"></param>
        /// <returns></returns>
        private static Legacy.Instruction[] ReorderInstructions(Legacy.Instruction[] instructions)
        {
            Legacy.Instruction[] newInstructions = new Legacy.Instruction[instructions.Length];
            List<Legacy.Instruction> boolInstructions = new List<Legacy.Instruction>();
            
            for (int i = 0; i < instructions.Length; i++)
            {
                if (instructions[i].Variable.Type == typeof(bool))
                {
                    boolInstructions.Add(instructions[i]);
                    continue;
                }
                
                newInstructions[i - boolInstructions.Count] = instructions[i];
            }

            int boolStartIndex = newInstructions.Length - boolInstructions.Count;
            for (int i = 0; i < boolInstructions.Count; i++)
            {
                newInstructions[boolStartIndex + i] = instructions[i];
            }
            
            return newInstructions;
        }

        public static bool TrySaveFolderInProjectPanel(string title, string folder, string defaultName, out string path)
        {
            ReadyPath($"{folder}/{defaultName}");
            
            string absPath = EditorUtility.SaveFolderPanel(title, folder, defaultName);
            if (absPath.Length == 0)
            {
                path = null;
                return false;
            }
            
            path = RelativePath(absPath);
            if (path == null)
                return false;
            
            return true;
        }
        
        public static bool PathInProject(string folderPath) => folderPath.StartsWith(Application.dataPath);

        public static string RelativePath(string folderPath)
        {
            if (!PathInProject(folderPath))
                return null;

            string projectPath = Application.dataPath;
            return folderPath.Remove(0, projectPath.Length - System.IO.Path.GetFileName(projectPath).Length);
        }
        
        public static void ReadyPath(string folderPath)
        {
            if (!System.IO.Directory.Exists(folderPath))
                System.IO.Directory.CreateDirectory(folderPath);
        }
    }
}
