using System;
using System.Collections.Generic;
using Deltin.Deltinteger.LanguageServer;
using Deltin.Deltinteger.WorkshopWiki;
using CompletionItem = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItem;
using CompletionItemKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItemKind;
using StringOrMarkupContent = OmniSharp.Extensions.LanguageServer.Protocol.Models.StringOrMarkupContent;

namespace Deltin.Deltinteger.Parse
{
    public abstract class DefinedFunction : IMethod, ICallable, IApplyBlock
    {
        public string Name { get; }
        public CodeType ReturnType { get; protected set; }
        public CodeParameter[] Parameters { get; private set; }
        public AccessLevel AccessLevel { get; protected set; }
        public Location DefinedAt { get; }
        public bool WholeContext { get; } = true;
        public StringOrMarkupContent Documentation { get; } = null;
        public MethodAttributes Attributes { get; } = new MethodAttributes();
        public bool Static { get; protected set; }

        protected ParseInfo parseInfo { get; }
        protected Scope methodScope { get; private set; }
        protected Scope containingScope { get; private set; }
        public Var[] ParameterVars { get; private set; }
        protected bool doesReturnValue;

        public CallInfo CallInfo { get; }

        public DefinedFunction(ParseInfo parseInfo, string name, Location definedAt)
        {
            Name = name;
            DefinedAt = definedAt;
            this.parseInfo = parseInfo;
            CallInfo = new CallInfo(this, parseInfo.Script);

            parseInfo.TranslateInfo.AddSymbolLink(this, definedAt, true);
            parseInfo.Script.AddCodeLensRange(new ReferenceCodeLensRange(this, parseInfo, CodeLensSourceType.Function, DefinedAt.range));
        }

        protected void SetupScope(Scope chosenScope)
        {
            methodScope = chosenScope.Child();
            containingScope = chosenScope;
        }

        // IApplyBlock
        public virtual void SetupParameters() {}
        public abstract void SetupBlock();

        protected void SetupParameters(DeltinScriptParser.SetParametersContext context, bool subroutineParameter)
        {
            var parameterInfo = CodeParameter.GetParameters(parseInfo, methodScope, context, subroutineParameter);
            Parameters = parameterInfo.Parameters;
            ParameterVars = parameterInfo.Variables;
        }

        public void Call(ScriptFile script, DocRange callRange)
        {
            script.AddDefinitionLink(callRange, DefinedAt);
            parseInfo.TranslateInfo.AddSymbolLink(this, new Location(script.Uri, callRange));
        }

        public virtual bool DoesReturnValue() => true;

        public string GetLabel(bool markdown) => HoverHandler.GetLabel(!doesReturnValue ? null : ReturnType?.Name ?? "define", Name, Parameters, markdown, null);

        public abstract IWorkshopTree Parse(ActionSet actionSet, MethodCall methodCall);

        public CompletionItem GetCompletion()
        {
            return new CompletionItem()
            {
                Label = Name,
                Kind = CompletionItemKind.Method
            };
        }

        protected List<IOnBlockApplied> listeners = new List<IOnBlockApplied>();
        public void OnBlockApply(IOnBlockApplied onBlockApplied)
        {
            listeners.Add(onBlockApplied);
        }
    }
}