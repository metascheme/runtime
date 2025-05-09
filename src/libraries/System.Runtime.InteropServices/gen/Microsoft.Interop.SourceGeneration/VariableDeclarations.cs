﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Microsoft.Interop.SyntaxFactoryExtensions;

namespace Microsoft.Interop
{
    public struct VariableDeclarations
    {
        public ImmutableArray<StatementSyntax> Initializations { get; init; }
        public ImmutableArray<LocalDeclarationStatementSyntax> Variables { get; init; }
        public static VariableDeclarations GenerateDeclarationsForManagedToUnmanaged(BoundGenerators marshallers, StubIdentifierContext context, bool initializeDeclarations)
        {
            ImmutableArray<StatementSyntax>.Builder initializations = ImmutableArray.CreateBuilder<StatementSyntax>();
            ImmutableArray<LocalDeclarationStatementSyntax>.Builder variables = ImmutableArray.CreateBuilder<LocalDeclarationStatementSyntax>();

            foreach (IBoundMarshallingGenerator marshaller in marshallers.NativeParameterMarshallers)
            {
                TypePositionInfo info = marshaller.TypeInfo;
                if (info.IsManagedReturnPosition)
                    continue;

                if (info.RefKind == RefKind.Out)
                {
                    initializations.Add(MarshallerHelpers.DefaultInit(info, context));
                }

                // Declare variables for parameters
                AppendVariableDeclarations(variables, marshaller, context, initializeToDefault: initializeDeclarations);
            }

            // Stub return is not the same as invoke return
            if (!marshallers.IsManagedVoidReturn)
            {
                // Declare variables for stub return value
                AppendVariableDeclarations(variables, marshallers.ManagedReturnMarshaller, context, initializeToDefault: initializeDeclarations);
            }

            if (!marshallers.IsUnmanagedVoidReturn && !marshallers.ManagedNativeSameReturn)
            {
                // Declare variables for invoke return value
                AppendVariableDeclarations(variables, marshallers.NativeReturnMarshaller, context, initializeToDefault: initializeDeclarations);
            }

            return new VariableDeclarations
            {
                Initializations = initializations.ToImmutable(),
                Variables = variables.ToImmutable()
            };

            static void AppendVariableDeclarations(ImmutableArray<LocalDeclarationStatementSyntax>.Builder statementsToUpdate, IBoundMarshallingGenerator marshaller, StubIdentifierContext context, bool initializeToDefault)
            {
                (string managed, string native) = context.GetIdentifiers(marshaller.TypeInfo);

                // Declare variable for return value
                if (marshaller.TypeInfo.IsManagedReturnPosition || marshaller.TypeInfo.IsNativeReturnPosition)
                {
                    statementsToUpdate.Add(Declare(
                        marshaller.TypeInfo.ManagedType.Syntax,
                        managed,
                        initializeToDefault));
                }

                // Declare variable with native type for parameter or return value
                if (marshaller.UsesNativeIdentifier)
                {
                    statementsToUpdate.Add(Declare(
                        marshaller.NativeType.Syntax,
                        native,
                        initializeToDefault));
                }
            }
        }

        public static VariableDeclarations GenerateDeclarationsForUnmanagedToManaged(BoundGenerators marshallers, StubIdentifierContext context, bool initializeDeclarations)
        {
            ImmutableArray<StatementSyntax>.Builder initializations = ImmutableArray.CreateBuilder<StatementSyntax>();
            ImmutableArray<LocalDeclarationStatementSyntax>.Builder variables = ImmutableArray.CreateBuilder<LocalDeclarationStatementSyntax>();

            foreach (IBoundMarshallingGenerator marshaller in marshallers.NativeParameterMarshallers)
            {
                TypePositionInfo info = marshaller.TypeInfo;
                if (info.IsNativeReturnPosition || info.IsManagedReturnPosition)
                    continue;

                // Declare variables for parameters
                AppendVariableDeclarations(variables, marshaller, context, initializeToDefault: initializeDeclarations);
            }

            if (!marshallers.IsManagedVoidReturn)
            {
                // Declare variables for stub return value
                AppendVariableDeclarations(variables, marshallers.ManagedReturnMarshaller, context, initializeToDefault: initializeDeclarations);
            }

            if (!marshallers.IsUnmanagedVoidReturn && !marshallers.ManagedNativeSameReturn)
            {
                // Declare variables for invoke return value
                AppendVariableDeclarations(variables, marshallers.NativeReturnMarshaller, context, initializeToDefault: initializeDeclarations);
            }

            return new VariableDeclarations
            {
                Initializations = initializations.ToImmutable(),
                Variables = variables.ToImmutable()
            };

            static void AppendVariableDeclarations(ImmutableArray<LocalDeclarationStatementSyntax>.Builder statementsToUpdate, IBoundMarshallingGenerator marshaller, StubIdentifierContext context, bool initializeToDefault)
            {
                (string managed, string native) = context.GetIdentifiers(marshaller.TypeInfo);

                // Declare variable for return value
                if (marshaller.TypeInfo.IsNativeReturnPosition)
                {
                    bool nativeReturnUsesNativeIdentifier = marshaller.UsesNativeIdentifier;

                    // Always initialize the return value.
                    statementsToUpdate.Add(Declare(
                        marshaller.TypeInfo.ManagedType.Syntax,
                        managed,
                        initializeToDefault || !nativeReturnUsesNativeIdentifier));

                    if (nativeReturnUsesNativeIdentifier)
                    {
                        statementsToUpdate.Add(Declare(
                            marshaller.NativeType.Syntax,
                            native,
                            initializeToDefault: true));
                    }
                }
                else
                {
                    ValueBoundaryBehavior boundaryBehavior = marshaller.ValueBoundaryBehavior;

                    // Declare variable with native type for parameter
                    // if the marshaller uses the native identifier and the signature uses a different identifier
                    // than the native identifier.
                    if (marshaller.UsesNativeIdentifier
                        && boundaryBehavior is not
                            (ValueBoundaryBehavior.NativeIdentifier or ValueBoundaryBehavior.CastNativeIdentifier))
                    {
                        TypeSyntax localType = marshaller.NativeType.Syntax;
                        if (boundaryBehavior != ValueBoundaryBehavior.AddressOfNativeIdentifier)
                        {
                            statementsToUpdate.Add(Declare(
                                localType,
                                native,
                                false));
                        }
                        else
                        {
                            // To simplify propogating back the value to the "byref" parameter,
                            // we'll just declare the native identifier as a ref to its type.
                            // The rest of the code we generate will work as expected, and we don't need
                            // to manually propogate back the updated values after the call.
                            statementsToUpdate.Add(Declare(
                                RefType(localType),
                                native,
                                marshaller.GenerateNativeByRefInitialization(context)));
                        }
                    }

                    // Declare a separate managed identifier when a separate managed and native identifier is needed
                    // and the marshaller is not the "managed exception" marshaller (whose managed identifier is defined by the catch clause).
                    if (boundaryBehavior != ValueBoundaryBehavior.ManagedIdentifier && !marshaller.TypeInfo.IsManagedExceptionPosition)
                    {
                        statementsToUpdate.Add(Declare(
                            marshaller.TypeInfo.ManagedType.Syntax,
                            managed,
                            initializeToDefault));
                    }
                }
            }
        }
    }
}
