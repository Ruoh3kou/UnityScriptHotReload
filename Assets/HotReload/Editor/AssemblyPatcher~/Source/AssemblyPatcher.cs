﻿/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using System.Linq;
using System.Text;
using SimpleJSON;
using dnlib;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using dnlib.DotNet.Emit;
using NHibernate.Mapping;
using TypeDef = dnlib.DotNet.TypeDef;
using dnlib.DotNet.MD;
using Remotion.Linq.Parsing.Structure.NodeTypeProviders;
using dnlib.DotNet.Writer;
using System.Diagnostics;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using System.Xml.Linq;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using NHibernate.Mapping.ByCode;
using System.Runtime.Loader;

namespace AssemblyPatcher;

public class HookedMethodInfo
{
    public MethodData baseMethod;
    public MethodData newMethod;
    public bool ilChanged;

    public HookedMethodInfo(MethodData baseMethod, MethodData newMethod, bool ilChanged)
    {
        this.baseMethod = baseMethod; this.newMethod = newMethod; this.ilChanged = ilChanged;
    }
}

public class MethodFixStatus
{
    public bool needHook;
    public bool ilFixed;
}

/// <summary>
/// 程序集构建器
/// </summary>
public class AssemblyPatcher
{
    /// <summary>
    /// 是否合法，要求baseAssDef中存在的类型和方法签名在newAssDef中必须存在，但newAssDef可以存在新增的类型和方法
    /// </summary>
    public bool isValid { get; private set; }
    public string moduleName { get; private set; }
    public AssemblyDataForPatch assemblyDataForPatch { get; private set; }

    private static TypeData _typeGenericMethodIndex, _typeGenericMethodWrapper;

    private MemberRef _ctorGenericMethodIndex, _ctorGenericMethodWrapper;

    private CorLibTypeSig   _voidTypeSig;
    private CorLibTypeSig   _int32TypeSig;
    private CorLibTypeSig   _stringTypeSig;
    private CorLibTypeSig   _objectTypeSig;
    private TypeSig         _typeTypeSig;
    private TypeSig         _typeArrayTypeSig;

    private Importer            _importer;
    private MethodPatcher       _methodPatcher;
    private GenericInstScanner  _genericInstScanner;
    private TypeDef             _wrapperClass;

    static AssemblyPatcher()
    {
        var shareCode = ModuleDefPool.GetModuleData("ShareCode");
        shareCode.types.TryGetValue("ScriptHotReload.GenericMethodIndexAttribute", out _typeGenericMethodIndex);
        shareCode.types.TryGetValue("ScriptHotReload.GenericMethodWrapperAttribute", out _typeGenericMethodWrapper);
    }

    public AssemblyPatcher(string moduleName)
    {
        this.moduleName = moduleName;
    }

    public bool DoPatch()
    {
        assemblyDataForPatch = new AssemblyDataForPatch(moduleName);
        assemblyDataForPatch.Init();

        if (!assemblyDataForPatch.isValid)
            return false;

        var patchDllDef = assemblyDataForPatch.patchDllData.moduleDef;
        _voidTypeSig = patchDllDef.CorLibTypes.Void;
        _int32TypeSig = patchDllDef.CorLibTypes.Int32;
        _stringTypeSig = patchDllDef.CorLibTypes.String;
        _objectTypeSig = patchDllDef.CorLibTypes.Object;
        _typeTypeSig = patchDllDef.Import(typeof(Type)).ToTypeSig();
        _typeArrayTypeSig = new SZArraySig(_typeTypeSig);

        _ctorGenericMethodIndex = patchDllDef.Import(_typeGenericMethodIndex.definition.FindDefaultConstructor());
        _ctorGenericMethodWrapper = patchDllDef.Import(_typeGenericMethodWrapper.definition.FindDefaultConstructor());

        _importer = new Importer(assemblyDataForPatch.patchDllData.moduleDef);
        _methodPatcher = new MethodPatcher(assemblyDataForPatch, _importer);
        _genericInstScanner = new GenericInstScanner(assemblyDataForPatch, _importer);

        _wrapperClass = assemblyDataForPatch.patchDllData.types["ScriptHotReload.__Patch_GenericInst_Wrapper__Gen__"].definition;

        // 扫描原始 dll 中的所有泛型实例
        _genericInstScanner.Scan();

        FixNewAssembly();
        GenGenericMethodWrappers();
        GenRuntimeGenericInstMethodInfosGetFunc();
        isValid = true;
        return isValid;
    }
    
    void FixNewAssembly()
    {
        int patchNo = GlobalConfig.Instance.patchNo;

        var processed = new Dictionary<MethodDef, MethodFixStatus>();
        foreach (var (_, methodData) in assemblyDataForPatch.patchDllData.allMethods)
        {
            _methodPatcher.PatchMethod(methodData.definition, processed, 0);
        }

        // 已存在类的静态构造函数需要清空，防止被二次调用
        if (processed.Count > 0)
        {
            var fixedType = new HashSet<TypeDef>();
            foreach(var kv in processed)
            {
                var status = kv.Value;
                if (status.ilFixed || status.needHook)
                    fixedType.Add(kv.Key.DeclaringType);
            }

            if(fixedType.Count > 0)
            {
                var constructors = new List<MethodDef>();
                var lambdaWrapperBackend = GlobalConfig.Instance.lambdaWrapperBackend;
                foreach (var tdef in fixedType)
                {
                    /*
                    * 这是编译器自动生成的 lambda 表达式静态类
                    * 由于代码修正时不会重定向其引用(自动编号的成员名称无法精确匹配)，因此需要保留其静态函数初始化代码
                    */
                    if (tdef.FullName.Contains("<>c"))
                        continue;

                    // 新定义的类型静态构造函数即使执行也是第一次执行，因此逻辑只能修正不能移除
                    if (assemblyDataForPatch.addedTypes.ContainsKey(tdef.FullName))
                        continue;

                    foreach(var mdef in tdef.Methods)
                    {
                        if (mdef.IsConstructor && mdef.IsStatic && mdef.HasBody)
                            constructors.Add(mdef);
                    }
                }
                StaticConstructorsQuickReturn(constructors);
            }

#if SCRIPT_PATCH_DEBUG
            StringBuilder sb = new StringBuilder();
            foreach (var kv in processed)
            {
                bool ilChanged = false;
                if (assemblyData.allBaseMethods.TryGetValue(kv.Key.FullName, out MethodData methodData))
                    ilChanged = methodData.ilChanged;

                sb.AppendLine(kv.Key + (ilChanged ? " [Changed]" : "") + (kv.Value.needHook ? " [Hook]" : "") + (kv.Value.ilFixed ? " [Fix]" : ""));
            }

            Debug.Log($"<color=yellow>Patch Methods of `{_baseAssDef.Name.Name}`: </color>{sb}");
#endif
        }


    }

    /// <summary>
    /// 修正被Hook或者被Fix的类型的静态构造函数，将它们改为直接返回, 否则其逻辑会在Patch里再次执行导致逻辑错误
    /// </summary>
    /// <param name="constructors"></param>
    void StaticConstructorsQuickReturn(List<MethodDef> constructors)
    {
        foreach (var ctor in constructors)
        {
            if (ctor.Name != ".cctor" || !ctor.HasBody)
                continue;

            // 直接移除会导致pdb找不到指令，因此直接在指令最前面插入一个ret指令
            var ins = ctor.Body.Instructions;
            ins.Insert(0, OpCodes.Ret.ToInstruction());
        }
    }

    int _wrapperIndex = 0;
    /// <summary>
    /// 为泛型方法生成wrapper函数，以避免Hook后StackWalk时crash
    /// </summary>
    void GenGenericMethodWrappers()
    {
        foreach(var genMethodData in _genericInstScanner.genericMethodDatas)
        {
            AddCAGenericIndex(genMethodData.genericMethodInPatch, _wrapperIndex);
            var genInstArgs = genMethodData.genericInsts;
            for (int i = 0, imax = genInstArgs.Count; i < imax; i++)
            {
                var typeGenArgs = genInstArgs[i].typeGenArgs;
                var methodGenArgs = genInstArgs[i].methodGenArgs;

                var wrapperMethod = Utils.GenWrapperMethodBody(genMethodData.genericMethodInPatch, _wrapperIndex, i, _importer, _wrapperClass, typeGenArgs, methodGenArgs);
                AddCAGenericMethodWrapper(wrapperMethod, _wrapperIndex, typeGenArgs, methodGenArgs);
                genInstArgs[i].wrapperMethodDef = wrapperMethod; // 记录 wrapperMethodDef 定义
            }
            _wrapperIndex++;
        }
    }

    /// <summary>
    /// 给泛型方法添加 [GenericMethodIndex]
    /// </summary>
    /// <param name="method"></param>
    /// <param name="idx"></param>
    void AddCAGenericIndex(MethodDef method, int idx)
    {
        var argIdx = new CAArgument(_int32TypeSig, idx);
        var nameArgIdx = new CANamedArgument(true, _int32TypeSig, "index", argIdx);
        var ca = new CustomAttribute(_ctorGenericMethodIndex, new CANamedArgument[] { nameArgIdx });
        method.CustomAttributes.Add(ca);
    }

    /// <summary>
    /// 给wrapper方法添加 [GenericMethodWrapper]
    /// </summary>
    void AddCAGenericMethodWrapper(MethodDef method, int idx, IList<TypeSig> typeGenArgs, IList<TypeSig> methodGenArgs)
    {
        List<CAArgument> caTypes = new List<CAArgument>();

        foreach (var t in typeGenArgs)
        {
            caTypes.Add(new CAArgument(_typeTypeSig, t));
        }

        foreach (var t in methodGenArgs)
        {
            caTypes.Add(new CAArgument(_typeTypeSig, t));
        }

        var argTypes = new CAArgument(_typeArrayTypeSig, caTypes);
        var nameArgTypes = new CANamedArgument(true, _typeArrayTypeSig, "typeGenArgs", argTypes);

        AddCAGenericMethodWrapper(method, idx, nameArgTypes);
    }

    void AddCAGenericMethodWrapper(MethodDef method, int idx, CANamedArgument typeArgs)
    {
        var argIdx = new CAArgument(_int32TypeSig, idx);
        var nameArgIdx = new CANamedArgument(true, _int32TypeSig, "index", argIdx);
        var caArgs  = new CANamedArgument[] { nameArgIdx, typeArgs };
        var ca = new CustomAttribute(_ctorGenericMethodWrapper, caArgs);
        method.CustomAttributes.Add(ca);
    }

    public class DictionaryDefInfos
    {
        public IMethod genFunc;

        public TypeSig dicTypeSig;
        public IMethod dicCtor;
        public IMethod dicAdd;
    }

    DictionaryDefInfos GetDictionaryDefInfos()
    {
        var ret = new DictionaryDefInfos();
        ret.genFunc = _wrapperClass.FindMethod("GetGenericInstMethodForPatch");
        ret.dicTypeSig = (ret.genFunc as MethodDefMD).ReturnType; // Dictionary<MethodBase, MethodBase>

        TypeSpec dicType = ret.dicTypeSig.ToTypeDefOrRef() as TypeSpec;

        var genericDicType = dicType.ResolveTypeDef();
        var genericDicCtor = genericDicType.FindDefaultConstructor();
        var genericDicAdd = genericDicType.FindMethod("Add");

        ret.dicCtor = new MemberRefUser(dicType.Module, ".ctor", genericDicCtor.MethodSig, dicType);
        ret.dicAdd = new MemberRefUser(dicType.Module, ".Add", genericDicAdd.MethodSig, dicType);
        return ret;
    }

    /// <summary>
    /// 生成运行时动态获取Base Dll内的泛型实例方法与Patch Dll内的Wrapper方法关联的函数
    /// </summary>
    void GenRuntimeGenericInstMethodInfosGetFunc()
    {
        var dicDefInfos = GetDictionaryDefInfos();
        var genFunc = dicDefInfos.genFunc.ResolveMethodDef();

        genFunc.Body = new CilBody();
        genFunc.Body.MaxStack = 4;
        var instructions = genFunc.Body.Instructions;

        instructions.Add(Instruction.Create(OpCodes.Newobj, dicDefInfos.dicCtor));    // newobj Dictionary<MethodInfo, MethodInfo>.ctor()
        
        foreach(var genericMethodData in _genericInstScanner.genericMethodDatas)
        {
            foreach(var instArgs in genericMethodData.genericInsts)
            {
                var importedBaseInstMethod = _importer.Import(instArgs.instMethodInBase);
                instructions.Add(Instruction.Create(OpCodes.Dup));                                  // dup  (dicObj->this)
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, importedBaseInstMethod));      // ldtoken key
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, instArgs.wrapperMethodDef));   // ldtoken value
                instructions.Add(Instruction.Create(OpCodes.Callvirt, dicDefInfos.dicAdd));            // callvirt Add
            }
        }

        instructions.Add(Instruction.Create(OpCodes.Ret));                                          // ret
    }

    public void WriteToFile()
    {
        string patchPath = assemblyDataForPatch.patchDllData.moduleDef.Location;
        string patchPdbPath = Path.ChangeExtension(patchPath, ".pdb");

        string tmpPath = $"{Path.GetDirectoryName(patchPath)}/tmp_{new Random().Next(100)}.dll";
        string tmpPdbPath = Path.ChangeExtension(tmpPath, ".pdb");

        var opt = new ModuleWriterOptions(assemblyDataForPatch.patchDllData.moduleDef) { WritePdb = true };
        assemblyDataForPatch.patchDllData.moduleDef.Write(tmpPath, opt);

        // 重命名 dll 名字
        assemblyDataForPatch.patchDllData.Unload();
        File.Delete(patchPath);
        File.Delete(patchPdbPath);
        File.Move(tmpPath, patchPath);
        File.Move(tmpPdbPath, patchPdbPath);
    }


}