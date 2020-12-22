﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace WebAssembly.Runtime.Compilation
{
    internal sealed class CompilationContext
    {
        private TypeBuilder? ExportsBuilder;
        private ILGenerator? generator;
        public readonly CompilerConfiguration Configuration;

        public CompilationContext(CompilerConfiguration configuration)
        {
            this.Configuration = configuration;
        }

        public void Reset(
            ILGenerator generator,
            Signature signature,
            WebAssemblyValueType[] locals
            )
        {
            this.generator = generator;
            this.Signature = signature;
            this.Locals = locals;

            this.Depth.Clear();
            {
                BlockType returnType;
                if (signature.RawReturnTypes.Length == 0)
                {
                    returnType = BlockType.Empty;
                }
                else
                {
                    switch (signature.RawReturnTypes[0])
                    {
                        default: //Should never happen.
                        case WebAssemblyValueType.Int32:
                            returnType = BlockType.Int32;
                            break;
                        case WebAssemblyValueType.Int64:
                            returnType = BlockType.Int64;
                            break;
                        case WebAssemblyValueType.Float32:
                            returnType = BlockType.Float32;
                            break;
                        case WebAssemblyValueType.Float64:
                            returnType = BlockType.Float64;
                            break;
                    }
                }
                this.Depth.Push(returnType);
            }
            this.Previous = OpCode.NoOperation;
            this.Labels.Clear();
            this.LoopLabels.Clear();
            this.Stack.Clear();
            this.BlockContexts.Clear();
            this.BlockContexts.Add(checked((uint)this.Depth.Count), new BlockContext());
        }

        public Signature[]? FunctionSignatures;

        public MethodInfo[]? Methods;

        public Signature[]? Types;

        public GlobalInfo[]? Globals;

        public readonly Dictionary<uint, MethodInfo> DelegateInvokersByTypeIndex = new Dictionary<uint, MethodInfo>();

        public readonly Dictionary<uint, MethodBuilder> DelegateRemappersByType = new Dictionary<uint, MethodBuilder>();

        public FieldBuilder? FunctionTable;

        internal const MethodAttributes HelperMethodAttributes =
            MethodAttributes.Private |
            MethodAttributes.Static |
            MethodAttributes.HideBySig
            ;

        private readonly Dictionary<HelperMethod, MethodBuilder> helperMethods = new Dictionary<HelperMethod, MethodBuilder>();

        public MethodInfo this[HelperMethod helper]
        {
            get
            {
                if (this.helperMethods.TryGetValue(helper, out var builder))
                    return builder;

                throw new InvalidOperationException(); // Shouldn't be possible.
            }
        }

        public MethodInfo this[HelperMethod helper, Func<HelperMethod, CompilationContext, MethodBuilder> creator]
        {
            get
            {
                if (this.helperMethods.TryGetValue(helper, out var builder))
                    return builder;

                this.helperMethods.Add(helper, builder = creator(helper, this));
                return builder;
            }
        }

        public Signature? Signature;

        public FieldBuilder? Memory;

        public WebAssemblyValueType[]? Locals;

        public readonly Stack<BlockType> Depth = new Stack<BlockType>();

        public OpCode Previous;

        public readonly Dictionary<uint, Label> Labels = new Dictionary<uint, Label>();

        public readonly HashSet<Label> LoopLabels = new HashSet<Label>();

        public readonly Stack<WebAssemblyValueType?> Stack = new Stack<WebAssemblyValueType?>();

        public readonly Dictionary<uint, BlockContext> BlockContexts = new Dictionary<uint, BlockContext>();

        public WebAssemblyValueType[] CheckedLocals => Locals ?? throw new InvalidOperationException();

        public Signature[] CheckedFunctionSignatures => FunctionSignatures ?? throw new InvalidOperationException();

        public MethodInfo[] CheckedMethods => Methods ?? throw new InvalidOperationException();

        public Signature[] CheckedTypes => Types ?? throw new InvalidOperationException();

        public FieldBuilder CheckedMemory => Memory ?? throw new InvalidOperationException();

        public TypeBuilder CheckedExportsBuilder
        {
            get => this.ExportsBuilder ?? throw new InvalidOperationException();
            set
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                this.ExportsBuilder = value;
            }
        }

        private ILGenerator CheckedGenerator => this.generator ?? throw new InvalidOperationException();

        public Signature CheckedSignature => this.Signature ?? throw new InvalidOperationException();

        public Label DefineLabel() => CheckedGenerator.DefineLabel();

        public void MarkLabel(Label loc) => CheckedGenerator.MarkLabel(loc);

        public void EmitLoadThis() => CheckedGenerator.EmitLoadArg(CheckedSignature.ParameterTypes.Length);

        public void Emit(System.Reflection.Emit.OpCode opcode) => CheckedGenerator.Emit(opcode);

        public void Emit(System.Reflection.Emit.OpCode opcode, byte arg) => CheckedGenerator.Emit(opcode, arg);

        public void Emit(System.Reflection.Emit.OpCode opcode, int arg) => CheckedGenerator.Emit(opcode, arg);

        public void Emit(System.Reflection.Emit.OpCode opcode, long arg) => CheckedGenerator.Emit(opcode, arg);

        public void Emit(System.Reflection.Emit.OpCode opcode, float arg) => CheckedGenerator.Emit(opcode, arg);

        public void Emit(System.Reflection.Emit.OpCode opcode, double arg) => CheckedGenerator.Emit(opcode, arg);

        public void Emit(System.Reflection.Emit.OpCode opcode, Label label) => CheckedGenerator.Emit(opcode, label);

        public void Emit(System.Reflection.Emit.OpCode opcode, Label[] labels) => CheckedGenerator.Emit(opcode, labels);

        public void Emit(System.Reflection.Emit.OpCode opcode, FieldInfo field) => CheckedGenerator.Emit(opcode, field);

        public void Emit(System.Reflection.Emit.OpCode opcode, MethodInfo meth) => CheckedGenerator.Emit(opcode, meth);

        public void Emit(System.Reflection.Emit.OpCode opcode, ConstructorInfo con) => CheckedGenerator.Emit(opcode, con);

        public LocalBuilder DeclareLocal(Type localType) => CheckedGenerator.DeclareLocal(localType);

        /// <summary>
        /// Pop multiple types from stack and test whether they match with expected types.
        /// The algorithm is based on the validation algorithm described in WASM spec.
        /// See: https://webassembly.github.io/spec/core/appendix/algorithm.html
        /// </summary>
        /// <param name="opcode">OpCode of the instruction (for exception message).</param>
        /// <param name="expectedTypes">Array of expected types (or null, which indicates any type is accepted), in </param>
        /// <returns>Array of actually popped types (or null, which indicates unknown type).</returns>
        public WebAssemblyValueType?[] PopStack(OpCode opcode, params WebAssemblyValueType?[] expectedTypes)
        {
            var actualTypes = new List<WebAssemblyValueType?>();
            var initialStackSize = this.Stack.Count;
            var blockContext = this.BlockContexts[checked((uint)this.Depth.Count)];

            foreach (var expected in expectedTypes)
            {
                WebAssemblyValueType? type;

                if (this.Stack.Count <= blockContext.InitialStackSize)
                {
                    if (this.IsUnreachable())
                    {
                        //unreachable, br, br_table, return can "make up" arbitrary types
                        type = null;
                    }
                    else
                    {
                        throw new StackTooSmallException(opcode, expectedTypes.Length, initialStackSize);
                    }
                }
                else
                {
                    type = this.Stack.Pop();
                }

                if (type.HasValue)
                {
                    if (expected.HasValue && type != expected)
                        throw new StackTypeInvalidException(opcode, expected.Value, type.Value);
                }
                else
                {
                    type = expected;
                }

                actualTypes.Add(type);
            }

            return actualTypes.ToArray();
        }

        public WebAssemblyValueType?[] PeekStack(OpCode opcode, params WebAssemblyValueType?[] expectedTypes)
        {
            var popped = this.PopStack(opcode, expectedTypes);
            foreach (var type in popped.Reverse())
            {
                this.Stack.Push(type);
            }

            return popped;
        }

        /// <summary>
        /// Marks the subseqwuent instructions are unreachable.
        /// </summary>
        public void MarkUnreachable(bool functionWide = false)
        {
            var blockContext = this.BlockContexts[checked((uint)this.Depth.Count)];
            blockContext.MarkUnreachable();

            if (functionWide)
            {
                for (var i = this.Depth.Count; i > 1; i--)
                {
                    this.BlockContexts[checked((uint)i)].MarkUnreachable();
                }
            }

            //Revert the stack state into beginning of the current block
            //This is based on the validation algorithm defined in WASM spec.
            //See: https://webassembly.github.io/spec/core/appendix/algorithm.html
            while (this.Stack.Count > blockContext.InitialStackSize)
            {
                this.Stack.Pop();
            }
        }

        public void MarkReachable()
        {
            this.BlockContexts[checked((uint)this.Depth.Count)].MarkReachable();
        }

        public bool IsUnreachable()
        {
            return this.BlockContexts[checked((uint)this.Depth.Count)].IsUnreachable;
        }
    }
}