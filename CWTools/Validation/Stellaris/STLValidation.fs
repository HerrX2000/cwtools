namespace CWTools.Validation.Stellaris
open CWTools.Validation.ValidationCore
open CWTools.Process.STLProcess
open CWTools.Process
open CWTools.Process.ProcessCore
open CWTools.Parser.Types
open CWTools.Process.STLScopes
open CWTools.Common
open CWTools.Common.STLConstants
open DotNet.Globbing
open CWTools.Games
open Newtonsoft.Json.Linq
open CWTools.Utilities.Utils
open System
open Microsoft.FSharp.Collections.Tagged
open System.Collections
open CWTools.Game.Stellaris.STLLookup


module STLValidation =
    type S = Severity
    type EntitySet<'T>(entities : struct (Entity * Lazy<'T>) list) =
        member __.GlobMatch(pattern : string) =
            let options = new GlobOptions();
            options.Evaluation.CaseInsensitive <- true;
            let glob = Glob.Parse(pattern, options)
            entities |> List.choose (fun struct (es, _) -> if glob.IsMatch(es.filepath) then Some es.entity else None)
        member this.GlobMatchChildren(pattern : string) =
            this.GlobMatch(pattern) |> List.map (fun e -> e.Children) |> List.collect id
        member __.AllOfType (entityType : EntityType) =
            entities |> List.choose(fun struct (es, d) -> if es.entityType = entityType then Some (es.entity, d)  else None)
        member this.AllOfTypeChildren (entityType : EntityType) =
            this.AllOfType(entityType) |> List.map (fun (e, d) -> e.Children) |> List.collect id
        member __.All = entities |> List.map (fun struct (es, _) -> es.entity)
        member __.AllWithData = entities |> List.map (fun struct (es, d) -> es.entity, d)
        member this.AllEffects= 
            let fNode = (fun (x : Node) acc ->
                            match x with
                            | :? EffectBlock as e -> e::acc
                            | :? Option as e -> e.AsEffectBlock::acc
                            |_ -> acc
                                )
                           
            this.All |> List.collect (foldNode7 fNode)
        member this.AllTriggers= 
            let fNode = (fun (x : Node) acc ->
                            match x with
                            | :? TriggerBlock as e -> e::acc
                            |_ -> acc
                                )
            this.All |> List.collect (foldNode7 fNode)
        member this.AllModifiers= 
            let fNode = (fun (x : Node) acc ->
                            match x with
                            | :? WeightModifierBlock as e -> e::acc
                            |_ -> acc
                                )
            this.All |> List.collect (foldNode7 fNode)
       


        member __.Raw = entities
        member this.Merge(y : EntitySet<'T>) = EntitySet(this.Raw @ y.Raw)

    type STLEntitySet = EntitySet<STLComputedData>
    type StructureValidator = EntitySet<STLComputedData> -> EntitySet<STLComputedData> -> ValidationResult
    type FileValidator = IResourceAPI<STLComputedData> -> EntitySet<STLComputedData> -> ValidationResult
    let shipName (ship : Ship) = if ship.Name = "" then Invalid [(inv (ErrorCodes.CustomError "must have name" Severity.Error) ship)] else OK
    let shipSize (ship : Ship) = if ship.ShipSize = "" then Invalid [(inv (ErrorCodes.CustomError "must have size" Severity.Error) ship)] else OK

    let validateShip : Validator<Ship>  = shipName <&> shipSize


    let getDefinedVariables (node : Node) =
        let fNode = (fun (x:Node) acc -> 
                        x.Values |> List.fold (fun a n -> if n.Key.StartsWith("@", StringComparison.OrdinalIgnoreCase) then n.Key::a else a) acc
                        )
        node |> (foldNode7 fNode) |> List.ofSeq

    let checkUsedVariables (node : Node) (variables : string list) =
        let fNode = (fun (x:Node) children ->
                        let values = x.Values |> List.choose (fun v -> match v.Value with |String s when s.StartsWith("@", StringComparison.OrdinalIgnoreCase) -> Some v |_ -> None)
                        match values with
                        | [] -> children
                        | x -> 
                            x |> List.map ((fun f -> f, f.Value.ToString()) >> (fun (l, v) -> if variables |> List.contains v then OK else Invalid [inv (ErrorCodes.UndefinedVariable v) l]))
                              |> List.fold (<&&>) children)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)
    
    

    let validateVariables : StructureValidator =
        fun os es ->
            let globalVars = os.GlobMatch("**/common/scripted_variables/*.txt") @ es.GlobMatch("**/common/scripted_variables/*.txt")
                            |> List.map getDefinedVariables
                            |> Seq.collect id |> List.ofSeq
            es.All <&!&>
            // let x =  
            //     es.All  
            //     |> List.map
                    (fun node -> 
                        let defined = getDefinedVariables node
                        let errors = checkUsedVariables node (defined @ globalVars)
                        errors
                    )
            //x |> List.fold (<&&>) OK

    let categoryScopeList = [
        ModifierCategory.Army, [Scope.Army; Scope.Planet; Scope.Country]
        ModifierCategory.Country, [Scope.Country]
        ModifierCategory.Leader, [Scope.Leader; Scope.Country]
        ModifierCategory.Megastructure, [Scope.Megastructure; Scope.Country]
        ModifierCategory.Planet, [Scope.Planet; Scope.Country]
        ModifierCategory.PlanetClass, [Scope.Planet; Scope.Pop; Scope.Country]
        ModifierCategory.Pop, [Scope.Pop; Scope.Planet; Scope.Country]
        ModifierCategory.PopFaction, [Scope.PopFaction; Scope.Country]
        ModifierCategory.Science, [Scope.Ship; Scope.Country]
        ModifierCategory.Ship, [Scope.Ship; Scope.Starbase; Scope.Fleet; Scope.Country]
        ModifierCategory.ShipSize, [Scope.Ship; Scope.Starbase; Scope.Country]
        ModifierCategory.Starbase, [Scope.Starbase; Scope.Country]
        ModifierCategory.Tile, [Scope.Tile; Scope.Pop; Scope.Planet; Scope.Country]
    ]

    let inline checkCategoryInScope (modifier : string) (scope : Scope) (node : ^a) (cat : ModifierCategory) =
        match List.tryFind (fun (c, _) -> c = cat) categoryScopeList, scope with
        |None, _ -> OK
        |Some _, s when s = Scope.Any -> OK
        |Some (c, ss), s -> if List.contains s ss then OK else Invalid [inv (ErrorCodes.IncorrectStaticModifierScope modifier (s.ToString()) (ss |> List.map (fun f -> f.ToString()) |> String.concat ", ")) node]
        

    let inline valStaticModifier (modifiers : Modifier list) (scopes : ScopeContext) (modifier : string) (node) =
        let exists = modifiers |> List.tryFind (fun m -> m.tag = modifier && not m.core )
        match exists with
        |None -> Invalid [inv (ErrorCodes.UndefinedStaticModifier modifier) node]
        |Some m -> m.categories <&!&>  (checkCategoryInScope modifier scopes.CurrentScope node)

    let valNotUsage (node : Node) = if (node.Values.Length + node.Children.Length) > 1 then Invalid [inv ErrorCodes.IncorrectNotUsage node] else OK

    let valTriggerLeafUsage (modifiers : Modifier list) (scopes : ScopeContext) (leaf : Leaf) =
        match leaf.Key with
        | "has_modifier" -> valStaticModifier modifiers scopes (leaf.Value.ToRawString()) leaf
        | _ -> OK
    
    let valTriggerNodeUsage (modifiers : Modifier list) (scopes : ScopeContext) (node : Node) =
        match node.Key with
        | "NOT" -> valNotUsage node
        | _ -> OK
    let valEffectLeafUsage (modifiers : Modifier list) (scopes : ScopeContext) (leaf : Leaf) =
        match leaf.Key with
        | "remove_modifier" -> valStaticModifier modifiers scopes (leaf.Value.ToRawString()) leaf
        | _ -> OK

    let valEffectNodeUsage (modifiers : Modifier list) (scopes : ScopeContext) (node : Node) =
        match node.Key with
        | "add_modifier" -> valStaticModifier modifiers scopes (node.TagText "modifier") node
        | _ -> OK

    let eventScope (event : Event) =
        match event.Key with
        |"country_event" -> Scope.Country
        |"fleet_event" -> Scope.Fleet
        |"ship_event" -> Scope.Ship
        |"pop_faction_event" -> Scope.PopFaction
        |"pop_event" -> Scope.Pop
        |"planet_event" -> Scope.Planet
        |_ -> Scope.Army

    let inline handleUnknownTrigger (root : ^a) (key : string) =
        match STLProcess.ignoreKeys |> List.tryFind (fun k -> k == key) with
        |Some _ -> OK //Do better
        |None -> if key.StartsWith("@", StringComparison.OrdinalIgnoreCase) then OK else Invalid [inv (ErrorCodes.UndefinedTrigger key) root]
    
    let inline handleUnknownEffect root (key : string) =
        match STLProcess.ignoreKeys |> List.tryFind (fun k -> k == key) with
        |Some _ -> OK //Do better
        |None -> if key.StartsWith("@", StringComparison.OrdinalIgnoreCase) then OK else Invalid [inv (ErrorCodes.UndefinedEffect key) root]
    
    let valTriggerLeaf (triggers : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (leaf : Leaf) =
        match triggers.TryFind leaf.Key with
        |Some (:? ScopedEffect as e) -> Invalid [inv (ErrorCodes.IncorrectScopeAsLeaf (e.Name) (leaf.Value.ToRawString())) leaf]
        |Some e ->
            if e.Scopes |> List.contains(scopes.CurrentScope) || scopes.CurrentScope = Scope.Any
            then valTriggerLeafUsage modifiers scopes leaf
            else Invalid [inv (ErrorCodes.IncorrectTriggerScope leaf.Key (scopes.CurrentScope.ToString()) (e.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", ")) leaf]
        |None -> handleUnknownTrigger leaf leaf.Key

    let valEffectLeaf (effects : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (leaf : Leaf) =
        match effects.TryFind leaf.Key with
            |Some (:? ScopedEffect as e) -> Invalid [inv (ErrorCodes.IncorrectScopeAsLeaf (e.Name) (leaf.Value.ToRawString())) leaf]
            |Some e ->
                if e.Scopes |> List.contains(scopes.CurrentScope) || scopes.CurrentScope = Scope.Any
                then valEffectLeafUsage modifiers scopes leaf
                else Invalid [inv (ErrorCodes.IncorrectEffectScope leaf.Key (scopes.CurrentScope.ToString()) (e.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", ")) leaf]
            |None -> handleUnknownEffect leaf leaf.Key

    let rec valEventTrigger (root : Node) (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (effect : Child) =
        match effect with
        |LeafC leaf -> valTriggerLeaf triggers modifiers scopes leaf
        |NodeC node ->
            match node.Key with
            |x when STLProcess.toTriggerBlockKeys |> List.exists (fun t -> t == x) ->
                valNodeTriggers root triggers effects modifiers scopes [] node
            |x when ["else"] |> List.exists (fun t -> t == x) ->
                valNodeTriggers node triggers effects modifiers scopes [] node

            // |x when STLProcess.isTargetKey x ->  
            //     valNodeTriggers root triggers effects Scope.Any node
            // |x when x.Contains("event_target:") ->

            //     OK //Handle later
            |x when x.Contains("parameter:") ->
                OK //Handle later
            |x ->
                match changeScope effects triggers x scopes with
                |NewScope (s, ignores) -> 
                    valNodeTriggers root triggers effects modifiers s ignores node
                    <&&>
                    valTriggerNodeUsage modifiers scopes node
                |WrongScope ss -> Invalid [inv (ErrorCodes.IncorrectScopeScope x (scopes.CurrentScope.ToString()) (ss |> List.map (fun s -> s.ToString()) |> String.concat ", ")) node]
                |NotFound ->
                    match triggers.TryFind x with
                    |Some e ->
                        if e.Scopes |> List.contains(scopes.CurrentScope) || scopes.CurrentScope = Scope.Any
                        then valTriggerNodeUsage modifiers scopes node
                        else Invalid [inv (ErrorCodes.IncorrectTriggerScope x (scopes.CurrentScope.ToString()) (e.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", ")) node]
                    // |Some (_, true) -> OK
                    // |Some (t, false) -> Invalid [inv S.Error node (sprintf "%s trigger used in incorrect scope. In %A but expected %s" x scopes.CurrentScope (t.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", "))]
                    |None -> handleUnknownTrigger node x
        |_ -> OK

    and valEventEffect (root : Node) (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (effect : Child) =
        match effect with
        |LeafC leaf -> valEffectLeaf effects modifiers scopes leaf
        |NodeC node ->
            match node.Key with
            |x when STLProcess.toTriggerBlockKeys |> List.exists (fun t -> t == x) ->
                valNodeTriggers root triggers effects modifiers scopes [] node
            |x when ["else"] |> List.exists (fun t -> t == x) ->
                valNodeEffects node triggers effects modifiers scopes [] node
            // |x when STLProcess.isTargetKey x ->
            //     OK //Handle later
            // |x when x.Contains("event_target:") ->
            //     OK //Handle later
            |x when x.Contains("parameter:") ->
                OK //Handle later
            |x ->
                match changeScope effects triggers x scopes with
                |NewScope (s, ignores) -> valNodeEffects node triggers effects modifiers s ignores node
                |WrongScope ss -> Invalid [inv (ErrorCodes.IncorrectScopeScope x (scopes.CurrentScope.ToString()) (ss |> List.map (fun s -> s.ToString()) |> String.concat ", ")) node]
                |NotFound ->
                    match effects.TryFind x with
                    |Some e -> 
                        if e.Scopes |> List.contains(scopes.CurrentScope) || scopes.CurrentScope = Scope.Any
                        then valEffectNodeUsage modifiers scopes node
                        else Invalid [inv (ErrorCodes.IncorrectEffectScope x (scopes.CurrentScope.ToString()) (e.Scopes  |> List.map (fun f -> f.ToString()) |> String.concat ", ")) node]
                    // |Some(_, true) -> OK
                    // |Some (t, false) -> Invalid [inv S.Error node (sprintf "%s effect used in incorrect scope. In %A but expected %s" x scopes.CurrentScope (t.Scopes  |> List.map (fun f -> f.ToString()) |> String.concat ", "))]
                    |None -> handleUnknownEffect node x
        |_ -> OK
    
    and valNodeTriggers (root : Node) (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (ignores : string list) (node : Node) =
        // let scopedTriggers = triggers |> List.map (fun (e, _) -> e, scopes.CurrentScope = Scope.Any || e.Scopes |> List.exists (fun s -> s = scopes.CurrentScope))
        // let scopedEffects = effects |> List.map (fun (e, _) -> e,  scopes.CurrentScope = Scope.Any || e.Scopes |> List.exists (fun s -> s = scopes.CurrentScope)) 
        let filteredAll = 
            node.All
            |> List.filter (function |NodeC c -> not (List.exists (fun i -> i == c.Key) ignores) |LeafC c -> not (List.exists (fun i -> i == c.Key) ignores) |_ -> false)
        List.map (valEventTrigger root triggers effects modifiers scopes) filteredAll |> List.fold (<&&>) OK

    and valNodeEffects (root : Node) (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (ignores : string list) (node : Node) =
        //let scopedTriggers = triggers |> List.map (fun (e, _) -> e, scopes.CurrentScope = Scope.Any || e.Scopes |> List.exists (fun s -> s = scopes.CurrentScope)) 
        //let scopedEffects = effects |> List.map (fun (e, _) -> e, scopes.CurrentScope = Scope.Any || e.Scopes |> List.exists (fun s -> s = scopes.CurrentScope)) 
        let filteredAll = 
            node.All
            |> List.filter (function |NodeC c -> not (List.exists (fun i -> i == c.Key) ignores) |LeafC c -> not (List.exists (fun i -> i == c.Key) ignores) |_ -> false)
        List.map (valEventEffect root triggers effects modifiers scopes) filteredAll |> List.fold (<&&>) OK
            

    let valOption (root : Node) (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (scopes : ScopeContext) (node : Node) =
        let optionTriggers = ["trigger"; "allow"]
        let optionEffects = ["tooltip"; "hidden_effect"]
        let optionExcludes = ["name"; "custom_tooltip"; "response_text"; "is_dialog_only"; "sound"; "ai_chance"; "custom_gui"; "default_hide_option"]
        let filterFunction =
            function
            | NodeC n -> optionExcludes |> List.exists (fun f -> n.Key == f)
            | LeafC l -> optionExcludes |> List.exists (fun f -> l.Key == f)
            | _ -> false
        let leaves = node.Values |> List.filter (fun l -> not (optionExcludes |> List.exists (fun f -> l.Key == f)))
        let children = node.Children |> List.filter (fun c -> not (optionExcludes |> List.exists (fun f -> c.Key == f)))
        let lres = leaves <&!&> (valEffectLeaf effects modifiers scopes)
        let tres = children |> List.filter (fun c -> optionTriggers |> List.exists (fun f -> c.Key == f))
                    <&!&> (NodeC >> valEventTrigger root triggers effects modifiers scopes)
        let effectPos = children |> List.filter (fun c -> not (optionTriggers |> List.exists (fun f -> c.Key == f)))
        let eres = effectPos |> List.filter (fun c -> not (optionEffects |> List.exists (fun f -> c.Key == f)))
                    <&!&> (NodeC >> valEventEffect root triggers effects modifiers scopes)
        let esres = effectPos |> List.filter (fun c -> optionEffects |> List.exists (fun f -> c.Key == f))
                    <&!&> (valNodeEffects root triggers effects modifiers scopes [])
        lres <&&> tres <&&> eres <&&> esres
        //children |> List.map (valEventEffect node triggers effects scopes) |> List.fold (<&&>) OK
        
    
    // let valEventTriggers  (triggers : (Effect) list) (effects : (Effect) list) (modifiers : Modifier list) (event : Event) =
    //     let eventScope = { Root = eventScope event; From = []; Scopes = [eventScope event]}
    //     //let eventScope = Seq.append [eventScope event] (Seq.initInfinite (fun _ -> Scope.Any))
    //     // let scopedTriggers = triggers |> List.map (fun e -> e, e.Scopes |> List.exists (fun s -> s = eventScope.CurrentScope)) 
    //     // let scopedEffects = effects |> List.map (fun e -> e, e.Scopes |> List.exists (fun s -> s = eventScope.CurrentScope)) 
    //     match event.Child "trigger" with
    //     |Some n -> 
    //         let v = List.map (valEventTrigger event triggers effects modifiers eventScope) n.All
    //         v |> List.fold (<&&>) OK
    //     |None -> OK
    /// Flawed, caqn't deal with DU        
    let rec copy source target =
        let properties (x:obj) = x.GetType().GetProperties()
        query {
            for s in properties source do
            join t in properties target on (s.Name = t.Name)
            where t.CanWrite
            select s }
        |> Seq.iter (fun s ->
            let value = s.GetValue(source,null)
            if value.GetType().FullName.StartsWith("System.") 
            then s.SetValue(target, value, null)            
            else copy value (s.GetValue(target,null))
        )



    let valEffectsNew (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (effectBlock : Node) =
        let scope = { Root = effectBlock.Scope; From = []; Scopes = [effectBlock.Scope]}
        effectBlock.All <&!&> valEventEffect effectBlock triggers effects modifiers scope

    let valAllEffects (triggers : (Effect) list) (effects : (Effect) list) (modifiers : Modifier list) (es : STLEntitySet) =
        let effectMap = effects |> List.map (fun e -> e.Name, e) |> (fun l -> EffectMap.FromList(STLStringComparer(), l))
        let triggerMap = triggers |> List.map (fun t -> t.Name, t) |> (fun l -> EffectMap.FromList(STLStringComparer(), l))
        let fNode = (fun (x : Node) children ->
            match x with
            | (:? EffectBlock as x) -> valEffectsNew triggerMap effectMap modifiers x
            | (:? Option as x) -> x |> filterOptionToEffects |> (fun o -> valEffectsNew triggerMap effectMap modifiers o)
            | _ -> OK
            <&&> children)
        let fseNode = (fun (x : Node) ->
            
            let scope = { Root = Scope.Any; From = []; Scopes = [Scope.Any]}
            x.Children <&!&> (fun f -> valEventEffect x triggerMap effectMap modifiers scope (NodeC f)))
        let fstNode = (fun (x : Node)  ->
            let scope = { Root = Scope.Any; From = []; Scopes = [Scope.Any]}
            x.Children <&!&> (fun f -> valEventTrigger x triggerMap effectMap modifiers scope (NodeC f)))

        let fCombine = (<&&>)
        // let opts = es.All |> List.collect (foldNode2 foNode (@) []) |> List.map filterOptionToEffects
        // es.All @ opts <&!&> foldNode2 fNode fCombine OK 
        //<&&>
        let b = (es.All <&!&> foldNode2 fNode fCombine OK)
        //<&&>
        // let c = (es.AllOfTypeChildren EntityType.ScriptedEffects <&!&> fseNode)
        // //<&&>
        // let d = (es.AllOfTypeChildren EntityType.ScriptedTriggers <&!&> fstNode)
        b //<&&> c <&&> d


    let valTriggersNew (triggers : EffectMap) (effects : EffectMap) (modifiers : Modifier list) (effectBlock : Node) =
        let scope = { Root = effectBlock.Scope; From = []; Scopes = [effectBlock.Scope]}
        effectBlock.All <&!&> valEventTrigger effectBlock triggers effects modifiers scope


    let valAllTriggers (triggers : (Effect) list) (effects : (Effect) list) (modifiers : Modifier list) (es : STLEntitySet) =
        let effectMap = effects |> List.map (fun e -> e.Name, e) |> (fun l -> EffectMap.FromList(STLStringComparer(), l))
        let triggerMap = triggers |> List.map (fun t -> t.Name, t) |> (fun l -> EffectMap.FromList(STLStringComparer(), l))
        let fNode = (fun (x : Node) children ->
            match x with
            | (:? TriggerBlock as x) when not x.InEffectBlock -> valTriggersNew triggerMap effectMap modifiers x
            | _ -> OK
            <&&> children)
        let fCombine = (<&&>)
        es.All <&!&> foldNode2 fNode fCombine OK 

    let valAfterOptionBug (event : Event) =
        let fNode = (fun (x : Node) (children : bool) ->
            (x.Values |> List.exists (fun v -> v.Key == "response_text") ) || children
            )
        let hasresponse = event |> foldNode2 fNode ((||)) false
        let hasafter = event.Children |> List.exists (fun v -> v.Key == "after")
        if hasresponse && hasafter then Invalid [inv (ErrorCodes.CustomError "This event uses after and has an option with response_text, this is bugged in 2.0.2" Severity.Warning) event] else OK
    /// Make sure an event either has a mean_time_to_happen or is stopped from checking all the time
    /// Not mandatory, but performance reasons, suggested by Caligula
    /// Check "mean_time_to_happen", "is_triggered_only", "fire_only_once" and "trigger = { always = no }".
    /// Create issue if none are true
    let valEventVals (event : Event) =
        let isMTTH = event.Has "mean_time_to_happen"
        let isTrig = event.Has "is_triggered_only"
        let isOnce = event.Has "fire_only_once"
        let isAlwaysNo = 
            match event.Child "trigger" with
            | Some t -> 
                match t.Tag "always" with
                | Some (Bool b) when b = false -> true
                | _ -> false
            | None -> false
        let e = 
            match isMTTH || isTrig || isOnce || isAlwaysNo with
            | false -> Invalid [inv ErrorCodes.EventEveryTick event]
            | true -> OK
        e <&&> valAfterOptionBug event

    let valResearchLeader (area : string) (cat : string option) (node : Node) =
        let fNode = (fun (x:Node) children ->
                        let results = 
                            match x.Key with
                            | "research_leader" ->
                                match x.TagText "area" with
                                | "" -> Invalid [inv ErrorCodes.ResearchLeaderArea x]
                                | area2 when area <> area2 -> Invalid [inv (ErrorCodes.ResearchLeaderTech area area2) x]
                                | _ -> OK
                                /// These aren't really required
                                // <&&>
                                // match cat, x.TagText "has_trait" with
                                // | None, _ -> OK
                                // | _, "" -> Invalid [inv S.Error x "This research_leader is missing required \"has_trait\""]
                                // | Some c, t when ("leader_trait_expertise_" + c) <> t -> Invalid [inv S.Warning x "This research_leader has the wrong expertise"]
                                // | _ -> OK
                            | _ -> OK
                        results <&&> children)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)

    let valTechnology : StructureValidator =
        fun _ es ->
            let techs = es.GlobMatchChildren("**/common/technology/*.txt")
            let inner =
                fun (node : Node) ->
                    let area = node.TagText "area"
                    let cat = node.Child "category" |> Option.bind (fun c -> c.All |> List.tryPick (function |LeafValueC lv -> Some (lv.Value.ToString()) |_ -> None))
                    let catres = 
                        match cat with
                        | None -> Invalid [inv ErrorCodes.TechCatMissing node]
                        | Some _ -> OK
                    catres <&&> valResearchLeader area cat node
            techs <&!&> inner
            //techs |> List.map inner |> List.fold (<&&>) OK

    let valButtonEffects : StructureValidator =
        fun os es ->
            let effects = (os.GlobMatchChildren("**/common/button_effects/*.txt"))
                            |> List.filter (fun e -> e :? Button_Effect)
                            |> List.map (fun e -> e.Key)
            let buttons = es.GlobMatchChildren("**/interface/*.gui") @ es.GlobMatchChildren("**/interface/**/*.gui")
            let fNode = (fun (x : Node) children ->
                            let results =
                                match x.Key with
                                | "effectButtonType" -> 
                                    x.Leafs "effect" <&!&> (fun e -> if List.contains (e.Value.ToRawString()) effects then OK else Invalid [inv (ErrorCodes.ButtonEffectMissing (e.Value.ToString())) e])
                                | _ -> OK
                            results <&&> children
                                )
            let fCombine = (<&&>)
            buttons <&!&> (foldNode2 fNode fCombine OK)

    let valSprites : StructureValidator = 
        //let spriteKeys = ["spriteType"; "portraitType"; "corneredTileSpriteType"; "flagSpriteType"]
        fun os es ->
            let sprites = os.GlobMatchChildren("**/interface/*.gfx") @ os.GlobMatchChildren("**/interface/*/*.gfx")
                            |> List.filter (fun e -> e.Key = "spriteTypes")
                            |> List.collect (fun e -> e.Children)
            let spriteNames = sprites |> Seq.collect (fun s -> s.TagsText "name") |> List.ofSeq
            let gui = es.GlobMatchChildren("**/interface/*.gui") @ es.GlobMatchChildren("**/interface/*/*.gui")
            let fNode = (fun (x : Node) children ->
                            let results =
                                match x.Leafs "spriteType" |> List.ofSeq with
                                | [] -> OK
                                | xs -> 
                                    xs <&!&> (fun e -> if List.contains (e.Value.ToRawString()) spriteNames then OK else Invalid [inv (ErrorCodes.SpriteMissing (e.Value.ToString())) e])
                            results <&&> children
                                )
            let fCombine = (<&&>)
            gui <&!&> (foldNode2 fNode fCombine OK)
    
    let valSpriteFiles : FileValidator =
        fun rm es ->
            let sprites = es.GlobMatchChildren("**/interface/*.gfx") @ es.GlobMatchChildren("**/interface/*/*.gfx")
                            |> List.filter (fun e -> e.Key = "spriteTypes")
                            |> List.collect (fun e -> e.Children)
            let filenames = rm.GetResources() |> List.choose (function |FileResource (f, _) -> Some f |EntityResource (f, _) -> Some f)
            let inner = 
                fun (x : Node) ->
                   Seq.append (x.Leafs "textureFile") (Seq.append (x.Leafs "texturefile") (x.Leafs "effectFile"))
                    <&!&> (fun l ->
                        let filename = l.Value.ToRawString().Replace("/","\\")
                        let filenamefallback = filename.Replace(".lua",".shader").Replace(".tga",".dds")
                        match filenames |> List.exists (fun f -> f.EndsWith(filename) || f.EndsWith(filenamefallback)) with
                        | true -> OK
                        | false -> Invalid [inv (ErrorCodes.MissingFile (l.Value.ToRawString())) l])
            sprites <&!&> inner



    let findAllSetVariables (node : Node) =
        let keys = ["set_variable"; "change_variable"; "subtract_variable"; "multiply_variable"; "divide_variable"]
        let fNode = (fun (x : Node) acc ->
                    x.Children |> List.fold (fun a n -> if List.contains (n.Key) keys then n.TagText "which" :: a else a) acc
                     )
        foldNode7 fNode node |> List.ofSeq

    let  validateUsedVariables (variables : string list) (node : Node) =
        let fNode = (fun (x : Node) children ->
                    match x.Childs "check_variable" |> List.ofSeq with
                    | [] -> children
                    | t -> 
                        t <&!&> (fun node -> node |> (fun n -> n.Leafs "which" |> List.ofSeq) <&!&> (fun n -> if List.contains (n.Value.ToRawString()) variables then OK else Invalid [inv (ErrorCodes.UndefinedScriptVariable (n.Value.ToRawString())) node] ))
                        <&&> children
                    )
        let fCombine = (<&&>)
        foldNode2 fNode fCombine OK node

    let getDefinedScriptVariables (es : STLEntitySet) =
        let fNode = (fun (x : Node) acc ->
                    match x with
                    | (:? EffectBlock as x) -> x::acc
                    | _ -> acc
                    )
        let ftNode = (fun (x : Node) acc ->
                    match x with
                    | (:? TriggerBlock as x) -> x::acc
                    | _ -> acc
                    )
        let foNode = (fun (x : Node) acc ->
                    match x with
                    | (:? Option as x) -> x::acc
                    | _ -> acc
                    )
        let opts = es.All |> List.collect (foldNode7 foNode) |> List.map filterOptionToEffects
        let effects = es.All |> List.collect (foldNode7 fNode) |> List.map (fun f -> f :> Node)
        //effects @ opts |> List.collect findAllSetVariables  
        es.AllEffects |> List.collect findAllSetVariables

    let getEntitySetVariables (e : Entity) =
        let fNode = (fun (x : Node) acc ->
                    match x with
                    | (:? EffectBlock as x) -> x::acc
                    | _ -> acc
                    )
        let foNode = (fun (x : Node) acc ->
                    match x with
                    | (:? Option as x) -> x::acc
                    | _ -> acc
                    )
        let opts = e.entity |> (foldNode7 foNode) |> List.map filterOptionToEffects |> List.map (fun n -> n :> Node)
        let effects = e.entity |> (foldNode7 fNode) |> List.map (fun f -> f :> Node)
        effects @ opts |> List.collect findAllSetVariables

    let valVariables : StructureValidator =
        fun os es ->
            let ftNode = (fun (x : Node) acc ->
                    match x with
                    | (:? TriggerBlock as x) -> x::acc
                    | _ -> acc
                    )
            let triggers = es.All |> List.collect (foldNode7 ftNode) |> List.map (fun f -> f :> Node)
            let defVars = (os.AllWithData @ es.AllWithData) |> List.collect (fun (_, d) -> d.Force().setvariables)
            //let defVars = effects @ opts |> List.collect findAllSetVariables
            triggers <&!&> (validateUsedVariables defVars)


    let valTest : StructureValidator = 
        fun os es ->
            let fNode = (fun (x : Node) acc ->
                        match x with
                        | (:? EffectBlock as x) -> x::acc
                        | _ -> acc
                        )
            let ftNode = (fun (x : Node) acc ->
                        match x with
                        | (:? TriggerBlock as x) -> x::acc
                        | _ -> acc
                        )
            let foNode = (fun (x : Node) acc ->
                        match x with
                        | (:? Option as x) -> x::acc
                        | _ -> acc
                        )
            let opts = es.All |> List.collect (foldNode7 foNode) |> List.map filterOptionToEffects
            let effects = es.All |> List.collect (foldNode7 fNode) |> List.map (fun f -> f :> Node)
            let triggers = es.All |> List.collect (foldNode7 ftNode) |> List.map (fun f -> f :> Node)
            OK
            // opts @ effects <&!&> (fun x -> Invalid [inv (ErrorCodes.CustomError "effect") x])
            // <&&> (triggers <&!&> (fun x -> Invalid [inv (ErrorCodes.CustomError "trigger") x]))
            
    let inline checkModifierInScope (modifier : string) (scope : Scope) (node : ^a) (cat : ModifierCategory) =
        match List.tryFind (fun (c, _) -> c = cat) categoryScopeList, scope with
        |None, _ -> OK
        |Some _, s when s = Scope.Any -> OK
        |Some (c, ss), s -> if List.contains s ss then OK else Invalid [inv (ErrorCodes.IncorrectModifierScope modifier (s.ToString()) (ss |> List.map (fun f -> f.ToString()) |> String.concat ", ")) node]

    let valModifier (modifiers : Modifier list) (scope : Scope) (leaf : Leaf) =
        match modifiers |> List.tryFind (fun m -> m.tag == leaf.Key) with
        |None -> Invalid [inv (ErrorCodes.UndefinedModifier (leaf.Key)) leaf]
        |Some m -> 
            m.categories <&!&> checkModifierInScope (leaf.Key) (scope) leaf
            <&&> (leaf.Value |> (function |Value.Int x when x = 0 -> Invalid [inv (ErrorCodes.ZeroModifier leaf.Key) leaf] | _ -> OK))
            // match m.categories |> List.contains (modifierCategory) with
            // |true -> OK
            // |false -> Invalid [inv (ErrorCodes.IncorrectModifierScope (leaf.Key) (modifierCategory.ToString()) (m.categories.ToString())) leaf]


    let valModifiers (modifiers : Modifier list) (node : ModifierBlock) =
        let filteredModifierKeys = ["description"; "key"]
        let filtered = node.Values |> List.filter (fun f -> not (filteredModifierKeys |> List.exists (fun k -> k == f.Key)))
        filtered <&!&> valModifier modifiers node.Scope
    let valAllModifiers (modifiers : (Modifier) list) (es : STLEntitySet) =
        let fNode = (fun (x : Node) children ->
            match x with
            | (:? ModifierBlock as x) -> valModifiers modifiers x
            | _ -> OK
            <&&> children)
        let fCombine = (<&&>)
        es.All <&!&> foldNode2 fNode fCombine OK 

    let addGeneratedModifiers (modifiers : Modifier list) (es : STLEntitySet) =
        let ships = es.GlobMatchChildren("**/common/ship_sizes/*.txt")
        let shipKeys = ships |> List.map (fun f -> f.Key)
        let shipModifierCreate =
            (fun k -> 
            [
                {tag = "shipsize_"+k+"_build_speed_mult"; categories = [ModifierCategory.Starbase]; core = true }
                {tag = "shipsize_"+k+"_build_cost_mult"; categories = [ModifierCategory.Starbase]; core = true }
                {tag = "shipsize_"+k+"_upkeep_mult"; categories = [ModifierCategory.Ship]; core = true }
                {tag = "shipsize_"+k+"_hull_mult"; categories = [ModifierCategory.Ship]; core = true }
                {tag = "shipsize_"+k+"_hull_add"; categories = [ModifierCategory.Ship]; core = true }
            ])
        let shipModifiers = shipKeys |> List.collect shipModifierCreate

        let stratres = es.GlobMatchChildren("**/common/strategic_resources/*.txt")
        let srKeys = stratres |> List.map (fun f -> f.Key)
        let srModifierCreate = 
            (fun k ->
            [
                {tag = "static_resource_"+k+"_add"; categories = [ModifierCategory.Country]; core = true }
                {tag = "static_planet_resource_"+k+"_add"; categories = [ModifierCategory.Planet]; core = true }
                {tag = "tile_resource_"+k+"_mult"; categories = [ModifierCategory.Tile]; core = true }
                {tag = "country_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_federation_member_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_federation_member_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_subjects_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_subjects_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_strategic_resources_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_strategic_resources_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_planet_classes_resource_"+k+"_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "country_planet_classes_resource_"+k+"_max_mult"; categories = [ModifierCategory.Country]; core = true }
                {tag = "tile_building_resource_"+k+"_add"; categories = [ModifierCategory.Tile]; core = true }
                {tag = "tile_resource_"+k+"_add"; categories = [ModifierCategory.Tile]; core = true }
                {tag = "planet_resource_"+k+"_add"; categories = [ModifierCategory.Planet]; core = true }
                {tag = "country_resource_"+k+"_add"; categories = [ModifierCategory.Country]; core = true }
                {tag = "max_"+k; categories = [ModifierCategory.Country]; core = true }
            ])
        let srModifiers = srKeys |> List.collect srModifierCreate
        let planetclasses = es.GlobMatchChildren("**/common/planet_classes/*.txt")
        let pcKeys = planetclasses |> List.map (fun f -> f.Key)
        let pcModifiers = pcKeys |> List.map (fun k -> {tag = k+"_habitability"; categories = [ModifierCategory.PlanetClass]; core = true})
        let buildingTags = es.GlobMatch("**/common/building_tags/*.txt") |> List.collect (fun f -> f.LeafValues |> List.ofSeq)
        let buildingTagModifierCreate =
            (fun k -> 
            [
                {tag = k+"_construction_speed_mult"; categories = [ModifierCategory.Planet]; core = true }
                {tag = k+"_build_cost_mult"; categories = [ModifierCategory.Planet]; core = true }
            ])
        let buildingModifiers = buildingTags |> List.map (fun l -> l.Value.ToRawString())
                                             |> List.collect buildingTagModifierCreate
        let countryTypeKeys = es.GlobMatchChildren("**/common/country_types/*.txt") |> List.map (fun f -> f.Key)
        let countryTypeModifiers = countryTypeKeys |> List.map (fun k -> {tag = "damage_vs_country_type_"+k+"_mult"; categories = [ModifierCategory.Ship]; core = true})
        let speciesKeys = es.GlobMatchChildren("**/common/species_archetypes/*.txt")
                            |> List.filter (fun s -> not (s.Has "inherit_traits_from"))
                            |> List.map (fun s -> s.Key)
        let speciesModifiers = speciesKeys |> List.map (fun k -> {tag = k+"_species_trait_points_add"; categories = [ModifierCategory.Country]; core = true})                    
        shipModifiers @ srModifiers @ pcModifiers @ buildingModifiers @ countryTypeModifiers @ speciesModifiers @ modifiers



    let filterWeightBlockToTriggers (wmb : STLProcess.WeightModifierBlock) =
        let excludes = ["factor"; "add"; "weight"]
        let newWmb = WeightModifierBlock(wmb.Key, wmb.Position)
        copy wmb newWmb
        newWmb.All <- newWmb.All |> List.filter (function |LeafC l -> (not (List.contains l.Key excludes)) | _ -> true)
        newWmb.All <- newWmb.All |> List.filter (function |NodeC l -> (not (List.contains l.Key excludes)) | _ -> true)
        newWmb.Scope <- wmb.Scope
        // eprintfn "%A" (wmb.Values |> List.map (fun  c -> c.Key))
        // eprintfn "%A" (newWmb.Values |> List.map (fun  c -> c.Key))
        newWmb :> Node

    let validateModifierBlocks (triggers : (Effect) list) (effects : (Effect) list) (modifiers : Modifier list) (es : STLEntitySet) =
        let effectMap = effects |> List.map (fun e -> e.Name, e) |> (fun l -> EffectMap.FromList(STLStringComparer(), l))
        let triggerMap = triggers |> List.map (fun t -> t.Name, t) |> (fun l -> EffectMap.FromList(STLStringComparer(), l))
        let foNode = (fun (x : Node) children ->
            match x with
            | (:? WeightModifierBlock as x) -> x |> filterWeightBlockToTriggers |> (fun o -> valTriggersNew triggerMap effectMap modifiers o)
            | _ -> OK
            <&&> children)
        let fCombine = (<&&>)
        es.All <&!&> foldNode2 foNode fCombine OK

    let findAllSavedEventTargets (event : Node) =
        let fNode = (fun (x : Node) children ->
                        let inner (leaf : Leaf) = if leaf.Key == "save_event_target_as" || leaf.Key == "save_global_event_target_as" then Some (leaf.Value.ToRawString()) else None
                        (x.Values |> List.choose inner) @ children
                        )
        let fCombine = (@)
        event |> (foldNode2 fNode fCombine [])

    let findAllSavedEventTargetsInEntity (e : Entity) =
        let fNode = (fun (x : Node) acc ->
                    match x with
                    | (:? EffectBlock as x) -> x::acc
                    | _ -> acc
                    )
        let foNode = (fun (x : Node) acc ->
                    match x with
                    | (:? Option as x) -> x::acc
                    | _ -> acc
                    )
        let opts = e.entity |> (foldNode7 foNode) |> List.map filterOptionToEffects |> List.map (fun n -> n :> Node)
        let effects = e.entity |> (foldNode7 fNode) |> List.map (fun f -> f :> Node)
        effects @ opts |> List.collect findAllSavedEventTargets


    let computeSTLData (e : Entity) =
        let eventIds = if e.entityType = EntityType.Events then e.entity.Children |> List.choose (function | :? Event as e -> Some e.ID |_ -> None) else []
        {
            eventids = eventIds
            setvariables = getEntitySetVariables e
            savedeventtargets = findAllSavedEventTargetsInEntity e
        }

    let getTechnologies (es : STLEntitySet) =
        let techs = es.AllOfTypeChildren EntityType.Technology
        let inner = 
            fun (t : Node) ->
                let name = t.Key
                let prereqs = t.Child "prerequisites" |> Option.map (fun c -> c.LeafValues |> Seq.toList |> List.map (fun v -> v.Value.ToRawString()))
                                                      |> Option.defaultValue []
                name, prereqs      
        techs |> List.map inner

    let getAllTechPreqreqs (es : STLEntitySet) =
        let fNode =
            fun (t : Node) children ->
                let inner ls (l : Leaf) = if l.Key == "has_technology" then l.Value.ToRawString()::ls else ls
                t.Values |> List.fold inner children
        (es.AllTriggers |> List.map (fun t -> t :> Node)) @ (es.AllModifiers |> List.map (fun t -> t :> Node)) |> List.collect (foldNode7 fNode)

    
    let validateTechnologies : StructureValidator =
        fun os es ->
            let getPrereqs (b : Node) =
                match b.Child "prerequisites" with
                |None -> []
                |Some p -> 
                    p.LeafValues |> List.ofSeq |> List.map (fun lv -> lv.Value.ToRawString())
            let buildingPrereqs = os.AllOfTypeChildren EntityType.Buildings @ es.AllOfTypeChildren EntityType.Buildings |> List.collect getPrereqs
            let shipsizePrereqs = os.AllOfTypeChildren EntityType.ShipSizes @ es.AllOfTypeChildren EntityType.ShipSizes |> List.collect getPrereqs
            let sectPrereqs = os.AllOfTypeChildren EntityType.SectionTemplates @ es.AllOfTypeChildren EntityType.SectionTemplates |> List.collect getPrereqs
            let compPrereqs = os.AllOfTypeChildren EntityType.ComponentTemplates @ es.AllOfTypeChildren EntityType.ComponentTemplates |> List.collect getPrereqs
            let stratResPrereqs = os.AllOfTypeChildren EntityType.StrategicResources @ es.AllOfTypeChildren EntityType.StrategicResources |> List.collect getPrereqs
            let armyPrereqs = os.AllOfTypeChildren EntityType.Armies @ es.AllOfTypeChildren EntityType.Armies |> List.collect getPrereqs
            let edictPrereqs = os.AllOfTypeChildren EntityType.Edicts @ es.AllOfTypeChildren EntityType.Edicts |> List.collect getPrereqs
            let tileBlockPrereqs = os.AllOfTypeChildren EntityType.TileBlockers @ es.AllOfTypeChildren EntityType.TileBlockers |> List.collect getPrereqs
            let allPrereqs = buildingPrereqs @ shipsizePrereqs @ sectPrereqs @ compPrereqs @ stratResPrereqs @ armyPrereqs @ edictPrereqs @ tileBlockPrereqs @ getAllTechPreqreqs os @ getAllTechPreqreqs es
            let techChildren = getTechnologies os @ getTechnologies es
                                |> (fun l -> l |> List.map (fun (name, _) -> name, l |> List.exists (fun (_, ts2) -> ts2 |> List.contains name)))
                                |> List.filter snd
                                |> List.map fst
            let techs = es.AllOfTypeChildren EntityType.Technology
            let inner (t : Node) =
                let isPreReq = t.Has "prereqfor_desc"
                let isMod = t.Has "modifier"
                let hasChildren = techChildren |> List.contains t.Key
                let isUsedElsewhere = allPrereqs |> List.contains t.Key
                let isWeightZero = t.Tag "weight" |> (function |Some (Value.Int 0) -> true |_ -> false)
                let isWeightFactorZero = t.Child "weight_modifier" |> Option.map (fun wm -> wm.Tag "factor" |> (function |Some (Value.Float 0.00) -> true |_ -> false)) |> Option.defaultValue false
                let hasFeatureFlag = t.Has "feature_flags"
                if isPreReq || isMod || hasChildren || isUsedElsewhere || isWeightZero || isWeightFactorZero || hasFeatureFlag then OK else Invalid [inv (ErrorCodes.UnusedTech (t.Key)) t]
            techs <&!&> inner



    let validateShipDesigns : StructureValidator =
        fun os es ->
            let ship_designs = es.AllOfTypeChildren EntityType.GlobalShipDesigns
            let section_templates = os.AllOfTypeChildren EntityType.SectionTemplates @ es.AllOfTypeChildren EntityType.SectionTemplates
            let weapons = os.AllOfTypeChildren EntityType.ComponentTemplates @ es.AllOfTypeChildren EntityType.ComponentTemplates
            let getWeaponInfo (w : Node) =
                match w.Key with
                | "weapon_component_template" ->
                    Some (w.TagText "key", ("weapon", w.TagText "size"))
                | "strike_craft_component_template" ->
                    Some (w.TagText "key", ("strike_craft", w.TagText "size"))
                | "utility_component_template" ->
                    Some (w.TagText "key", ("utility", w.TagText "size"))
                | _ -> None
            let weaponInfo = weapons |> List.choose getWeaponInfo |> Map.ofList
            let getSectionInfo (s : Node) =
                match s.Key with
                | "ship_section_template" ->
                    let inner (n : Node) =  
                        n.TagText "name", (n.TagText "slot_type", n.TagText "slot_size")
                    let component_slots = s.Childs "component_slot" |> List.ofSeq |> List.map inner
                    let createUtilSlot (prefix : string) (size : string) (i : int) =
                        List.init i (fun i -> prefix + sprintf "%i" (i+1), ("utility", size))
                    let smalls = s.Tag "small_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "SMALL_UTILITY_" "small" i |_ -> [])
                    let med = s.Tag "medium_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "MEDIUM_UTILITY_" "medium" i |_ -> [])
                    let large = s.Tag "large_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "LARGE_UTILITY_" "large" i |_ -> [])
                    let aux = s.Tag "aux_utility_slots" |> (function |Some (Value.Int i) -> createUtilSlot "AUX_UTILITY_" "aux" i |_ -> [])
                    let all = (component_slots @ smalls @ med @ large @ aux ) |> Map.ofList
                    Some (s.TagText "key", all)
                | _ -> None
            let sectionInfo = section_templates |> List.choose getSectionInfo |> Map.ofList

            let validateComponent (section : string) (sectionMap : Collections.Map<string, (string * string)>) (c : Node) =
                let slot = c.TagText "slot"
                let slotFound = sectionMap |> Map.tryFind slot
                let template = c.TagText "template"
                let templateFound = weaponInfo |> Map.tryFind template
                match slotFound, templateFound with
                | None, _ -> Invalid [inv (ErrorCodes.MissingSectionSlot section slot) c]
                | _, None -> Invalid [inv (ErrorCodes.UnknownComponentTemplate template) c]
                | Some (sType, sSize), Some (tType, tSize) ->
                    if sType == tType && sSize == tSize then OK else Invalid [inv (ErrorCodes.MismatchedComponentAndSlot slot sSize template tSize) c]

            let defaultTemplates = [ "DEFAULT_COLONIZATION_SECTION"; "DEFAULT_CONSTRUCTION_SECTION"]            
            let validateSection (s : Node) =
                let section = s.TagText "template"
                if defaultTemplates |> List.contains section then OK else
                    let sectionFound = sectionInfo |> Map.tryFind section
                    match sectionFound with
                    | None -> Invalid [inv (ErrorCodes.UnknownSectionTemplate section) s]
                    | Some smap ->
                        s.Childs "component" <&!&> validateComponent section smap

            let validateDesign (d : Node) =
                d.Childs "section" <&!&> validateSection
            
            ship_designs <&!&> validateDesign

    let validateMixedBlocks : StructureValidator =
        fun _ es ->

            let fNode = (fun (x : Node) children ->
                if (x.LeafValues |> Seq.isEmpty |> not && (x.Leaves |> Seq.isEmpty |> not || x.Children |> Seq.isEmpty |> not)) |> not
                then children
                else Invalid [inv ErrorCodes.MixedBlock x] <&&> children
                )
            let fCombine = (<&&>)
            es.All <&!&> foldNode2 fNode fCombine OK


    let validateSolarSystemInitializers : StructureValidator =
        fun _ es ->
            let inits = es.AllOfTypeChildren EntityType.SolarSystemInitializers
            let starclasses =
                 es.AllOfTypeChildren EntityType.StarClasses
                |> List.map (fun sc -> sc.Key)
            let fNode =
                fun (x : Node) ->
                    match x.Has "class", starclasses |> List.contains (x.TagText "class") with
                    |true, true -> Invalid [inv (ErrorCodes.CustomError (sprintf "%s, %s" x.Key (x.TagText "class")) Severity.Warning) x]
                    |true, false -> Invalid [inv (ErrorCodes.CustomError (sprintf "%s, %s" x.Key (x.TagText "class")) Severity.Warning) x]
                    |false, _ -> Invalid [inv (ErrorCodes.CustomError "This initializer is missing a class" Severity.Error) x]
                    |_, false -> Invalid [inv (ErrorCodes.CustomError (sprintf "The star class %s does not exist" (x.TagText "class")) Severity.Error) x]
            inits <&!&> fNode