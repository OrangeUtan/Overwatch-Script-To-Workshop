﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.LanguageServer;
using Antlr4.Runtime;

namespace Deltin.Deltinteger.Parse
{
    public class ParsingData
    {
        private static readonly Log Log = new Log("Parse");

        public static ParsingData GetParser(string file, string content)
        {
            return new ParsingData(file, content);
        }

        private ParsingData(string file, string content)
        {
            Rule initialGlobalValues = new Rule(Constants.INTERNAL_ELEMENT + "Initial Global Values");
            Rule initialPlayerValues = new Rule(Constants.INTERNAL_ELEMENT + "Initial Player Values", RuleEvent.OngoingPlayer, Team.All, PlayerSelector.All);
            TranslateRule globalTranslate = new TranslateRule(initialGlobalValues, Root, this);
            TranslateRule playerTranslate = new TranslateRule(initialPlayerValues, Root, this);

            // The looper rule
            GlobalLoop = new Looper(true);
            PlayerLoop = new Looper(false);
            
            VarCollection = new VarCollection();
            Root = new ScopeGroup(VarCollection);
            UserMethods = new List<UserMethod>();
            DefinedTypes = new List<DefinedType>();

            GetObjects(content, file, globalTranslate, playerTranslate);

            foreach (var type in DefinedTypes)
                try
                {
                    type.RegisterParameters(this);
                }
                catch (SyntaxErrorException ex)
                {
                    Diagnostics.Error(ex);
                }
            foreach (var method in UserMethods)
                try
                {
                    method.RegisterParameters(this);
                }
                catch (SyntaxErrorException ex)
                {
                    Diagnostics.Error(ex);
                }
            
            if (!Diagnostics.ContainsErrors())
            {
                // Parse the rules.
                Rules = new List<Rule>();

                for (int i = 0; i < RuleNodes.Count; i++)
                {
                    try
                    {
                        var result = TranslateRule.GetRule(RuleNodes[i], Root, this);
                        Rules.Add(result);
                    }
                    catch (SyntaxErrorException ex)
                    {
                        Diagnostics.Error(ex);
                    }
                }

                globalTranslate.Finish();
                playerTranslate.Finish();

                // Add the player initial values rule if it was used.
                if (initialPlayerValues.Actions.Length > 0)
                    Rules.Insert(0, initialPlayerValues);
                
                // Add the global initial values rule if it was used.
                if (initialGlobalValues.Actions.Length > 0)
                    Rules.Insert(0, initialGlobalValues);
                
                // Add the looper rules if they were used.
                if (GlobalLoop.Used)
                    Rules.Add(GlobalLoop.Finalize());
                if (PlayerLoop.Used)
                    Rules.Add(PlayerLoop.Finalize());
            }

            Success = !Diagnostics.ContainsErrors();
        }

        private RulesetNode GetRuleset(string file, string content)
        {
            AntlrInputStream inputStream = new AntlrInputStream(content);

            // Lexer
            DeltinScriptLexer lexer = new DeltinScriptLexer(inputStream);
            CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);

            // Parse
            DeltinScriptParser parser = new DeltinScriptParser(commonTokenStream);
            var errorListener = new ErrorListener(file, Diagnostics);
            parser.RemoveErrorListeners();
            parser.AddErrorListener(errorListener);

            DeltinScriptParser.RulesetContext ruleSetContext = parser.ruleset();

            Log.Write(LogLevel.Verbose, ruleSetContext.ToStringTree(parser));

            // Get the ruleset node.
            RulesetNode ruleset = null;
            if (!Diagnostics.ContainsErrors())
            {
                BuildAstVisitor bav = new BuildAstVisitor(file, Diagnostics);
                ruleset = (RulesetNode)bav.Visit(ruleSetContext);
            }

            AdditionalErrorChecking aec = new AdditionalErrorChecking(file, parser, Diagnostics);
            aec.Visit(ruleSetContext);

            return ruleset;
        }

        private void GetObjects(string document, string file, TranslateRule globalTranslate, TranslateRule playerTranslate)
        {
            // If this file was already loaded, don't load it again.
            if (Imported.Contains(file)) return;
            Imported.Add(file);
            Diagnostics.AddFile(file);

            // Get the ruleset.
            RulesetNode ruleset = GetRuleset(file, document);

            if (RuleSetNode == null)
                RuleSetNode = ruleset;

            if (ruleset != null && !Diagnostics.ContainsErrors())
            {
                // Get the defined types
                foreach (var definedType in ruleset.DefinedTypes)
                    try
                    {
                        if (DefinedTypes.Any(type => type.Name == definedType.Name))
                            throw SyntaxErrorException.NameAlreadyDefined(definedType.Location);
                        DefinedTypes.Add(new DefinedType(definedType));
                    }
                    catch (SyntaxErrorException ex)
                    {
                        Diagnostics.Error(ex);
                    }

                // Get the variables
                foreach (var definedVar in ruleset.DefinedVars)
                    try
                    {
                        IndexedVar var;
                        if (definedVar.UseVar == null)
                            var = VarCollection.AssignVar(Root, definedVar.VariableName, definedVar.IsGlobal, definedVar);
                        else
                            var = VarCollection.AssignVar(
                                Root, 
                                definedVar.VariableName, 
                                definedVar.IsGlobal,
                                definedVar.UseVar.Variable, 
                                definedVar.UseVar.Index,
                                definedVar
                            );
                        if (definedVar.Type != null)
                            var.Type = GetDefinedType(definedVar.Type, definedVar.Location);

                        // Set initial values
                        if (definedVar.Value != null)
                            if (definedVar.IsGlobal)
                                globalTranslate.Actions.AddRange(var.SetVariable(globalTranslate.ParseExpression(Root, definedVar.Value)));
                            else
                                playerTranslate.Actions.AddRange(var.SetVariable(playerTranslate.ParseExpression(Root, definedVar.Value)));
                    }
                    catch (SyntaxErrorException ex)
                    {
                        Diagnostics.Error(ex);
                    }

                // Get the user methods.
                for (int i = 0; i < ruleset.UserMethods.Length; i++)
                    try
                    {
                        UserMethods.Add(new UserMethod(Root, ruleset.UserMethods[i]));
                    }
                    catch (SyntaxErrorException ex)
                    {
                        Diagnostics.Error(ex);
                    }

                // Get the rules
                RuleNodes.AddRange(ruleset.Rules);

                List<string> importedFiles = new List<string>();

                // Check the imported files.
                foreach (ImportNode importNode in ruleset.Imports)
                    try
                    {
                        string importFileName = Path.GetFileName(importNode.File);
                        string importFilePath;
                        try
                        {
                            importFilePath = Extras.CombinePathWithDotNotation(file, importNode.File);
                        }
                        catch (ArgumentException)
                        {
                            // ArgumentException is thrown if the filename has invalid characters.
                            throw SyntaxErrorException.InvalidImportPathChars(importNode.File, importNode.Location);
                        }

                        if (file == importFilePath)
                            throw SyntaxErrorException.SelfImport(importNode.Location);

                        // Syntax error if the file does not exist.
                        if (!System.IO.File.Exists(importFilePath))
                            throw SyntaxErrorException.ImportFileNotFound(importFilePath, importNode.Location);

                        // Warning if the file was already imported.
                        if (importedFiles.Contains(importFilePath))
                        {
                            Diagnostics.Warning(string.Format(SyntaxErrorException.alreadyImported, importFileName), importNode.Location);
                        }
                        else
                        {
                            string content = System.IO.File.ReadAllText(importFilePath);
                            GetObjects(content, importFilePath, globalTranslate, playerTranslate);
                            importedFiles.Add(importFilePath);
                        }
                    }
                    catch (SyntaxErrorException ex)
                    {
                        Diagnostics.Error(ex);
                    }               
            }
        }

        public Diagnostics Diagnostics { get; private set; } = new Diagnostics();
        public List<Rule> Rules { get; private set; }
        private List<RuleNode> RuleNodes { get; set; } = new List<RuleNode>();
        public List<DefinedType> DefinedTypes { get; private set; }
        public List<UserMethod> UserMethods { get; private set; }
        public bool Success { get; private set; }
        public VarCollection VarCollection { get; private set; } = new VarCollection();
        public ScopeGroup Root { get; private set; }
        public RulesetNode RuleSetNode { get; private set; }
        private Looper GlobalLoop { get; set; }
        private Looper PlayerLoop { get; set; }
        private List<string> Imported { get; } = new List<string>();

        public IMethod GetMethod(string name)
        {
            return (IMethod)UserMethods?.FirstOrDefault(um => um.Name == name) 
            ?? (IMethod)CustomMethodData.GetCustomMethod(name) 
            ?? (IMethod)Element.GetElement(name);
        }

        public DefinedType GetDefinedType(string name, Location location)
        {
            DefinedType type = DefinedTypes.FirstOrDefault(dt => dt.Name == name);
            if (type == null && location != null)
                throw SyntaxErrorException.NonexistentType(name, location);
            return type;
        }

        public Looper GetLooper(bool isGlobal)
        {
            return isGlobal? GlobalLoop : PlayerLoop;
        }
    }
}