using System;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor.Api;

public static partial class AvatarAgent
{
    // Adds a Modular Avatar toggle as a new child of the avatar root, laid out like the
    // existing Eyemask toggle: Object Toggle, Menu Installer and a synced, saved Menu Item
    // named after the object, on by default with defaultOn. edits[0] (kind "active", guarded
    // by expectedName) is the object it switches and the state it switches to. The object is
    // saved in the opposite state, which is what the toggle returns it to when off. One Undo step;
    // not saved.
    static Result AddToggle(GameObject avatar, Request req, Result result)
    {
        RequireDraft(avatar.scene.path);
        if (string.IsNullOrWhiteSpace(req.instanceName)) throw new ArgumentException("add-toggle requires instanceName (the menu name)");
        if (req.edits.Length != 1 || req.edits[0].kind != "active") throw new ArgumentException("add-toggle takes one edit of kind active");
        var edit = req.edits[0];
        var target = Find(avatar.transform, edit.path);
        if (string.IsNullOrEmpty(edit.expectedName) || target.name != edit.expectedName)
            throw new ArgumentException("The toggled object requires a matching expectedName");
        if (target == avatar.transform) throw new ArgumentException("Cannot toggle the avatar root");
        if (avatar.transform.Cast<Transform>().Any(c => c.name == req.instanceName))
            throw new InvalidOperationException("The avatar already has a child named " + req.instanceName);

        string label = "Avatar Agent: " + (string.IsNullOrWhiteSpace(req.label) ? "Add toggle " + req.instanceName : req.label);
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        try
        {
            var go = new GameObject(req.instanceName);
            Undo.RegisterCreatedObjectUndo(go, label);
            Undo.SetTransformParent(go.transform, avatar.transform, label);
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;

            var toggle = Undo.AddComponent<ModularAvatarObjectToggle>(go);
            var reference = new AvatarObjectReference();
            reference.Set(target.gameObject);
            toggle.Objects.Add(new ToggledObject { Object = reference, Active = edit.active });
            // Modular Avatar returns the object to its saved state when the toggle is off, so a target
            // saved in the switched state would never change. The scene holds the off state.
            Undo.RecordObject(target.gameObject, label);
            target.gameObject.SetActive(!edit.active);
            Undo.AddComponent<ModularAvatarMenuInstaller>(go);
            var item = Undo.AddComponent<ModularAvatarMenuItem>(go);
            item.Control = new VRCExpressionsMenu.Control
            {
                name = "",
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                value = 1,
                subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                labels = new VRCExpressionsMenu.Control.Label[0],
            };
            item.isSynced = true;
            item.isSaved = true;
            item.isDefault = req.defaultOn;

            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(avatar.scene);
            result.notes.Add($"{label}: switches {target.name} {(edit.active ? "on" : "off")} (synced, saved, default {(req.defaultOn ? "on" : "off")}; one Undo step; not saved)");
        }
        catch { Undo.RevertAllDownToGroup(group); throw; }
        finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
        result.sceneDirty = avatar.scene.isDirty;
        Inspect(avatar, result);
        return result;
    }

    // Adds a VRCFury slider (menu name instanceName) on a new child of the avatar root that
    // drives one blendshape from 0 to edits[0].weight. edits[0] is kind "blendshape" with path, expectedName and shape;
    // sliderDefault (0..1) is the starting value. Saved, so it keeps its value across worlds
    // (VRCFury's default is unsaved). One Undo step; the scene is not saved.
    static Result AddFurySlider(GameObject avatar, Request req, Result result)
    {
        RequireDraft(avatar.scene.path);
        if (string.IsNullOrWhiteSpace(req.instanceName)) throw new ArgumentException("add-fury-slider requires instanceName (the menu name)");
        if (req.edits.Length != 1 || req.edits[0].kind != "blendshape") throw new ArgumentException("add-fury-slider takes one edit of kind blendshape");
        if (req.sliderDefault < 0 || req.sliderDefault > 1) throw new ArgumentException("sliderDefault must be 0..1");
        var edit = req.edits[0];
        var target = Find(avatar.transform, edit.path);
        if (string.IsNullOrEmpty(edit.expectedName) || target.name != edit.expectedName)
            throw new ArgumentException("The blendshape renderer requires a matching expectedName");
        var skin = target.GetComponent<SkinnedMeshRenderer>();
        if (skin == null || skin.sharedMesh == null || skin.sharedMesh.GetBlendShapeIndex(edit.shape) < 0 || !float.IsFinite(edit.weight))
            throw new ArgumentException("Invalid blendshape/weight");
        if (avatar.transform.Cast<Transform>().Any(c => c.name == req.instanceName))
            throw new InvalidOperationException("The avatar already has a child named " + req.instanceName);

        string label = "Avatar Agent: " + (string.IsNullOrWhiteSpace(req.label) ? "Add slider " + req.instanceName : req.label);
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        try
        {
            var go = new GameObject(req.instanceName);
            go.transform.SetParent(avatar.transform, false);
            // VRCFury's public API assembly isn't referenced from Assets, so call it by reflection.
            var api = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("com.vrcfury.api.FuryComponents"))
                .FirstOrDefault(t => t != null) ?? throw new InvalidOperationException("VRCFury API not found");
            object Call(object target, string method, params object[] args) =>
                target.GetType().GetMethod(method).Invoke(target, args);
            var toggle = api.GetMethod("CreateToggle").Invoke(null, new object[] { go });
            Call(toggle, "SetMenuPath", req.instanceName);
            Call(toggle, "SetSlider", true);
            Call(Call(toggle, "GetActions"), "AddBlendshape", edit.shape, edit.weight, skin);
            Undo.RegisterCreatedObjectUndo(go, label);
            var fury = go.GetComponents<Component>().Single(c => c != null && c.GetType().FullName == "VF.Model.VRCFury");
            var so = new SerializedObject(fury);
            var value = so.FindProperty("content")?.FindPropertyRelative("defaultSliderValue");
            if (value == null) throw new InvalidOperationException("VRCFury toggle has no defaultSliderValue");
            value.floatValue = req.sliderDefault;
            var saved = so.FindProperty("content")?.FindPropertyRelative("saved")
                ?? throw new InvalidOperationException("VRCFury toggle has no saved field");
            saved.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(avatar.scene);
            result.notes.Add($"{label}: {target.name} {edit.shape} 0..{edit.weight}, default {req.sliderDefault}, saved (one Undo step; scene not saved)");
        }
        catch { Undo.RevertAllDownToGroup(group); throw; }
        finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
        result.sceneDirty = avatar.scene.isDirty;
        Inspect(avatar, result);
        return result;
    }

    // Saves the draft scene in place.
    static Result SaveDraft(GameObject avatar, Result result)
    {
        RequireDraft(avatar.scene.path);
        if (!EditorSceneManager.SaveScene(avatar.scene)) throw new System.IO.IOException("Scene save failed");
        result.sceneDirty = avatar.scene.isDirty;
        result.notes.Add("Saved " + avatar.scene.path);
        return result;
    }

    // Builds and uploads the avatar as an update of its existing blueprint, through the SDK
    // control panel's builder (the panel must be open and logged in). Never creates a new
    // avatar. The scene must be saved. Progress goes to the editor log.
    static async void Upload(Request req, Action<Result> done)
    {
        var result = new Result { command = req.command };
        IVRCSdkAvatarBuilderApi builder = null;
        EventHandler<string> buildLog = (_, s) => Debug.Log("[AvatarAgent upload] build: " + s);
        EventHandler<string> buildError = (_, s) => Debug.LogWarning("[AvatarAgent upload] build error: " + s);
        string lastStatus = null;
        EventHandler<(string status, float percentage)> uploadLog = (_, p) =>
        {
            if (p.status == lastStatus) return;
            lastStatus = p.status;
            Debug.Log($"[AvatarAgent upload] upload: {p.status} ({p.percentage:P0})");
        };
        EventHandler<string> uploadError = (_, s) => Debug.LogWarning("[AvatarAgent upload] upload error: " + s);
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Leave play mode before uploading");
            var avatar = Source(req.sourceId);
            RequireDraft(avatar.scene.path);
            if (!string.IsNullOrEmpty(req.scene) && avatar.scene.path != req.scene)
                throw new InvalidOperationException("Recipe scene does not match the loaded avatar scene");
            if (avatar.scene.isDirty) throw new InvalidOperationException("Save the scene before uploading");
            var id = avatar.GetComponent<PipelineManager>()?.blueprintId;
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("The avatar has no blueprint ID; refusing to create a new avatar");
            if (!VRCSdkControlPanel.TryGetBuilder(out builder))
            {
                // Open the panel ourselves and give it time to log in and register its builder.
                Debug.Log("[AvatarAgent upload] opening the VRChat SDK control panel");
                EditorWindow.GetWindow<VRCSdkControlPanel>();
                for (int i = 0; i < 120 && !VRCSdkControlPanel.TryGetBuilder(out builder); i++)
                    await System.Threading.Tasks.Task.Delay(500);
                if (builder == null) throw new InvalidOperationException("The VRChat SDK control panel did not become ready (logged in?) within 60 s");
            }

            var existing = await VRCApi.GetAvatar(id, true);
            builder.OnSdkBuildProgress += buildLog; builder.OnSdkBuildError += buildError;
            builder.OnSdkUploadProgress += uploadLog; builder.OnSdkUploadError += uploadError;
            Debug.Log("[AvatarAgent upload] starting build and upload of " + existing.Name);
            await builder.BuildAndUpload(avatar, existing);
            result.ok = true;
            result.notes.Add("Uploaded as an update of the existing avatar '" + existing.Name + "'");
            Debug.Log("[AvatarAgent upload] done");
        }
        catch (Exception e) { result.ok = false; result.error = e.ToString(); Debug.LogException(e); }
        finally
        {
            if (builder != null)
            {
                builder.OnSdkBuildProgress -= buildLog; builder.OnSdkBuildError -= buildError;
                builder.OnSdkUploadProgress -= uploadLog; builder.OnSdkUploadError -= uploadError;
            }
        }
        done(result);
    }
}
