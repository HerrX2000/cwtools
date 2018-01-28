namespace CWTools.Validation
open CWTools.Validation.ValidationCore
open CWTools.Process.STLProcess
open CWTools.Process
open CWTools.Process.ProcessCore
open CWTools.Parser
open CWTools.Process.STLScopes
open CWTools.Common
open CWTools.Common.STLConstants
open DotNet.Globbing
open CWTools.Validation.STLValidation
open System.Xml.Linq

module STLLocalisationValidation =
    type S = Severity
    type LocalisationValidator = EntitySet -> (Lang * Set<string>) list -> EntitySet -> ValidationResult

    let checkLocKey (leaf : Leaf) (keys : Set<string>) (lang : Lang) key =
        match key = "" || key.Contains(" "), Set.contains key keys with
        | true, _ -> OK
        | _, true -> OK
        | _, false -> Invalid [inv S.Warning leaf (sprintf "Localisation key %s is not defined for %O" key lang)]

    let checkLocKeys (keys : (Lang * Set<string>) list) (leaf : Leaf) =
        let key = leaf.Value |> (function |QString s -> s |s -> s.ToString())
        keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocKey leaf keys l key) OK

    let getLocKeys (keys : (Lang * Set<string>) list) (tags : string list) (node : Node) =
        let fNode = (fun (x:Node) children ->
                        let results =  x.Values |> List.filter (fun l -> tags |> List.contains l.Key) |> List.fold (fun s t -> s <&&> (checkLocKeys keys t) ) OK
                        results <&&> children)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)

    
    let valEventLocs (event : Event) (keys : (Lang * Set<string>) list) =
        let titles = event.Leafs "title" |> List.map (checkLocKeys keys) |> List.fold (<&&>) OK
        let options = event.Childs "option" |> List.collect (fun o -> o.Leafs "name" |> List.map (checkLocKeys keys))
                                            |> List.fold (<&&>) OK                
        let usedKeys = event.Children |> List.fold (fun s c -> s <&&> (getLocKeys keys ["desc"; "text"; "custom_tooltip"; "fail_text"; "response_text"] c)) OK
        titles <&&> options <&&> usedKeys

    let checkLocNode (node : Node) (keys : Set<string>) (lang : Lang) key =
        match key = "" || key.Contains(" "), Set.contains key keys with
        | true, _ -> OK
        | _, true -> OK
        | _, false -> Invalid [inv S.Warning node (sprintf "Localisation key %s is not defined for %O" key lang)]
        
    let checkKeyAndDesc (node : Node) (keys : (Lang * Set<string>) list) =
        let key = node.Key
        let desc = key + "_desc"
        let keyres = keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK
        let descres = keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK
        keyres <&&> descres
        
    let valTechLocs : LocalisationValidator =
        fun _ keys es ->
            let entities = es.GlobMatchChildren("**/common/technology/*.txt")
            entities |> List.map
                (fun (node : Node) ->
                let keyres = checkKeyAndDesc node keys
                let innerKeys = node.Childs "prereqfor_desc" |> List.fold (fun s c -> s <&&> (getLocKeys keys ["desc"; "title"] c)) OK
                let flags = node.Child "feature_flags" |> Option.map (fun c -> c.All |> List.choose (function |LeafValueI lv -> Some lv.Value |_ -> None )) |> Option.defaultValue []
                let flags2 = flags |> List.map (fun f -> "feature_" + f.ToString())
                let flagres = flags2 |> List.fold (fun s c -> s <&&> (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l c) OK)) OK
                let flagdesc = flags2 |> List.map (fun f -> f + "_desc")
                let flagdescres = flagdesc |> List.fold (fun s c -> s <&&> (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l c) OK)) OK
                let gateway = node.TagText "gateway"
                let gatewayres =
                    match gateway with
                    | "" -> OK
                    | x -> keys |> List.fold (fun state (l, keys) -> state <&&> checkLocNode node keys l ("gateway_" + x)) OK
                keyres <&&> innerKeys <&&> gatewayres <&&> flagres <&&> flagdescres
                )
                |> List.fold (<&&>) OK
        

    let valCompSetLocs : LocalisationValidator =
        fun _ keys es ->
            let entities = es.GlobMatchChildren("**/common/component_sets/*.txt")
            entities |> List.map
                (fun (node : Node) -> 
                    let key = node.Key
                    let required = node.Tag "required_component_set" |> (function |Some (Bool b) when b = true -> true |_ -> false)
                    match key, required with
                    | "component_set", false -> 
                        let ckey = node.TagText "key"
                        let ckeydesc = ckey + "_DESC"
                        let ckeyres =  keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l ckey) OK
                        let ckeydescres =  keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l ckeydesc) OK
                        ckeyres <&&> ckeydescres
                    | _ -> OK)
                |> List.fold (<&&>) OK
            


    let valCompTempLocs : LocalisationValidator = 
        fun _ keys es ->
            let entities = es.GlobMatchChildren("**/common/component_templates/*.txt")
            let inner = 
                fun (node : Node) ->
                    let keyres = node.Leafs "key" |> List.fold (fun s l -> s <&&> (checkLocKeys keys l)) OK
                    let auras = node.Childs "friendly_aura" @ node.Childs "hostile_aura"
                    let aurares = auras |> List.fold (fun s c -> s <&&> (getLocKeys keys ["name"] c)) OK
                    keyres <&&> aurares
            entities |> List.map inner |> List.fold (<&&>) OK


    let valBuildingLocs : LocalisationValidator =
        fun _ keys es ->
            let entities = es.GlobMatchChildren("**/common/buildings/*.txt")
            let inner =
                fun node ->
                    let keyres = checkKeyAndDesc node keys
                    let failtext = node.Children |> List.fold (fun s c -> s <&&> (getLocKeys keys ["fail_text"] c)) OK
                    keyres <&&> failtext
            entities |> List.map inner |> List.fold (<&&>) OK

    let valTraditionLocs (node : Node) (keys : (Lang * Set<string>) list) (starts : string list) (finals : string list) (traditions : string list)= 
        let key = node.Key
        let finishres = 
            match finals |> List.contains key with
            | true -> 
                let effect = key + "_effect"
                keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l effect) OK
            | false -> OK
        let adoptres =
            match starts |> List.contains key with
            | true ->
                (let effect = key + "_effect"
                keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l effect) OK)
                <&&>
                (let desc = key + "_desc"
                keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK)
            | false -> OK
        let traditionsres = 
            match traditions |> List.contains key with
            | true ->
                let desc = key + "_desc"
                let a = keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK
                let delayed = key + "_delayed"
                let b = keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l delayed) OK
                a <&&> b
            | false -> OK
        let keyres = keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK
        keyres <&&> finishres <&&> adoptres <&&> traditionsres

    let processTradCat  (keys : (Lang * Set<string>) list) (cat : Node) =
        let key = cat.Key
        let start = cat.TagText "adoption_bonus"
        let finish = cat.TagText "finish_bonus"
        let vals = cat.Child "traditions" |> Option.map (fun c -> c.All |> List.choose (function |LeafValueI lv -> Some lv.Value |_ -> None )) |> Option.defaultValue []
        let traditions = vals |> List.map (function |QString s -> s |x -> x.ToString())
        let keyres = keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode cat keys l key) OK
        (start, finish, traditions)

    let valTraditionLocCats : LocalisationValidator = 
        fun entitySet keys nes -> 
            let cats = entitySet.GlobMatch("**/tradition_categories/*.txt") |> List.collect (fun e -> e.Children)
            let newcats = nes.GlobMatch("**/tradition_categories/*.txt") |> List.collect (fun e -> e.Children)
            let starts, finishes, trads = cats |> List.map (processTradCat keys) |> List.fold (fun ( ss, fs, ts) (s, f, t) -> s::ss,  f::fs, ts @ t) ([], [], [])
            let traditions = nes.GlobMatch("**/traditions/*.txt")  |> List.collect (fun e -> e.Children)
            let inner = fun tradition -> valTraditionLocs tradition keys starts finishes trads
            let innerCat = fun (cat : Node) -> keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode cat keys l cat.Key) OK
            newcats |> List.map innerCat |> List.fold (<&&>) OK
            <&&>
            (traditions |> List.map inner |> List.fold (<&&>) OK)

    // let valTraditionLocCats (cats : Node list) (traditions : Node list) (keys : (Lang * Set<string>) list) =
    //     //eprintfn "%A" cats
    //     let catres, starts, finishes, trads = cats |> List.map (processTradCat keys) |> List.fold (fun (rs, ss, fs, ts) (r, s, f, t) -> rs <&&> r, s::ss,  f::fs, ts @ t) (OK, [], [], [])
    //     //eprintfn "%A %A %A" starts finishes trads
    //     let tradres = traditions |> List.fold (fun state trad -> state <&&> (valTraditionLocs trad keys starts finishes trads)) OK
    //     catres <&&> tradres

    let checkLocNodeKeyAdv (node : Node) keys prefix suffix = 
        let key = prefix + node.Key + suffix
        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
    
    let checkLocNodeKeyAdvs keys prefix suffixes node = suffixes |> List.fold (fun s c -> s <&&> (checkLocNodeKeyAdv node keys prefix c)) OK

    let (<&!&>) es f = es |> List.fold (fun s c -> s <&&> (f c)) OK

    let valArmiesLoc : LocalisationValidator =
        fun _ keys es ->
            let armies = es.GlobMatchChildren("**/common/armies/*.txt")
            let inner = checkLocNodeKeyAdvs keys "" [""; "_plural"; "_desc"]
            armies <&!&> inner

    let valArmyAttachmentLocs : LocalisationValidator =
        fun _ keys es ->
            let armies = es.GlobMatchChildren("**/common/army_attachments/*.txt")
            let inner = checkLocNodeKeyAdvs keys "army_attachment_" [""; "_desc"]
            armies <&!&> inner

    let valDiploPhrases : LocalisationValidator =
        fun _ keys es ->
            let diplos = es.GlobMatchChildren("**/common/diplo_phrases/*.txt")
            let rec inner =
                fun (node : Node) ->
                    match node.Key with
                    | "greetings"
                    | "select"
                    | "propose"
                    | "accept"
                    | "consider"
                    | "refuse"
                    | "propose_vote" ->
                         node.Children |> List.map (fun c -> keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode c keys l c.Key) OK)  |> List.fold (<&&>) OK
                    | _ -> node.Children |> List.map inner |> List.fold (<&&>) OK
                    
            diplos |> List.collect (fun n -> n.Children) |> List.map inner |> List.fold (<&&>) OK

    let valShipLoc : LocalisationValidator =
        fun _ keys es ->
            let ships = es.GlobMatchChildren("**/common/ship_sizes/*.txt")
            let inner1 = checkLocNodeKeyAdvs keys "" [""; "_plural"]
            let inner2 = checkLocNodeKeyAdvs keys "shipsize_" ["_construction_speed_mult"; "_build_cost_mult"; "_upkeep_mult"]
            ships <&!&> inner1
            <&&>
            (ships <&!&> inner2)

    let valFactionDemands : LocalisationValidator =
        fun _ keys es ->
            let factions = es.GlobMatchChildren("**/common/pop_faction_types/*.txt")
            let demands = factions |> List.collect (fun f -> f.Childs "demand")
            let inner = fun c -> (getLocKeys keys ["title"; "desc"; "unfulfilled_title"] c)
            let finner = checkLocNodeKeyAdvs keys "pft_" [""; "_desc"]
            demands <&!&> inner
            <&&>
            (factions <&!&> finner)


    let valSpeciesRightsLocs : LocalisationValidator =
        fun _ keys es ->
            let species = es.GlobMatchChildren("**/common/species_rights/*.txt")
            let inner = checkLocNodeKeyAdvs keys "" [""; "_tooltip";"_tooltip_delayed"] <&> getLocKeys keys ["text"; "fail_text"]
            species <&!&> inner


    let valMapsLocs : LocalisationValidator = 
        fun _ keys es ->
            let maps = es.GlobMatchChildren("**/map/setup_scenarios/*.txt")
            let inner =
                fun (node : Node) ->
                    let name  = node.TagText "name"
                    match name with
                    | "" -> OK
                    | x -> (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l x) OK)
            maps |> List.fold (fun s c -> s <&&> (inner c)) OK

    let valMegastructureLocs : LocalisationValidator = 
        fun _ keys es ->
            let megas = es.GlobMatchChildren("**/common/megastructures/*.txt")
            let inner =
                fun (node : Node) ->
                    let key = node.Key
                    let desc = key + "_DESC"
                    let details = key + "_MEGASTRUCTURE_DETAILS"
                    let delayed = key + "_CONSTRUCTION_INFO_DELAYED"
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
                    <&&>
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK)
                    <&&>
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l details) OK)
                    <&&>
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l delayed) OK)
            megas |> List.fold (fun s c -> s <&&> (inner c)) OK
    
    let valModifiers : LocalisationValidator =
        fun _ keys es ->
            let mods = es.GlobMatchChildren("**/common/static_modifiers/*.txt")
            mods <&!&> (checkLocNodeKeyAdvs keys "" [""])
            //TODO: Add desc back behind a "strict" flag
           // mods |> List.fold (fun s c -> s <&&> checkKeyAndDesc c keys) OK

    let valModules : LocalisationValidator = 
        fun _ keys es ->
            let mods = es.GlobMatchChildren("**/common/spaceport_modules/*.txt")
            let inner = 
                fun (node : Node) ->
                    let key = "sm_" + node.Key
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
            mods |> List.fold (fun s c -> s <&&> (inner c)) OK

    let valTraits : LocalisationValidator =
        fun _ keys es ->
            let traits = es.GlobMatchChildren("**/common/traits/*.txt")
            traits |> List.fold (fun s c -> s <&&> checkKeyAndDesc c keys) OK

    let valGoverments : LocalisationValidator =
        fun _ keys es ->
            let govs = es.GlobMatchChildren("**/common/governments/*.txt")
            let civics = es.GlobMatchChildren("**/common/governments/civics/*.txt")
            let ginner =
                fun (node : Node) ->
                    let key = node.Key
                    let keyres = (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
                    let titlesres = getLocKeys keys ["ruler_title"; "ruler_title_female"; "heir_title"; "heir_title_female"] node
                    keyres <&&> titlesres
            //let inner = fun (node : Node) -> (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l node.Key) OK)
            civics |> List.fold (fun s c -> s <&&> checkKeyAndDesc c keys) OK
            <&&>
            (civics |> List.fold (fun s c -> s <&&> (getLocKeys keys ["description"] c)) OK)
            <&&>
            (govs |> List.fold (fun s c -> s <&&> (ginner c)) OK)
            
    let valPersonalities : LocalisationValidator =
        fun _ keys es ->
            let pers = es.GlobMatchChildren("**/common/personalities/*.txt")
            let inner =
                fun (node : Node) ->
                    let key = "personality_" + node.Key
                    let desc = key + "_desc"
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
                    <&&>
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK)
            pers |> List.fold (fun s c -> s <&&> (inner c)) OK

    let valEthics : LocalisationValidator =
        fun _ keys es ->
            let ethics = es.GlobMatchChildren("**/common/ethics/*.txt")
            ethics  |> List.filter (fun e -> e.Key <> "ethic_categories")
                    |> List.fold (fun s c -> s <&&> checkKeyAndDesc c keys) OK

    let valPlanetClasses : LocalisationValidator =
        fun _ keys es ->
            let planets = es.GlobMatchChildren("**/common/planet_classes/*.txt")
            let inner =
                fun (node : Node) ->
                    let key = node.Key
                    let desc = key + "_desc"
                    let tile = key + "_tile"
                    let tiledesc = tile + "_desc"
                    let traitk = "trait_" + key + "_preference"
                    let traitdesc = traitk + "_desc"
                    let hab = key + "_habitability"
                    let colonizable = node.Tag "colonizable" |> (function |Some (Bool b) when b -> true |_ -> false)
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
                    <&&>
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK)
                    <&&>
                    if not colonizable then OK else
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l tile) OK)
                        <&&>
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l tiledesc) OK)
                        <&&>
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l traitk) OK)
                        <&&>
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l traitdesc) OK)
                        <&&>
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l hab) OK)
            planets 
                |> List.filter (fun n -> n.Key <> "random_list")
                |> List.fold (fun s c -> s <&&> (inner c)) OK

    let valEdicts : LocalisationValidator =
        fun _ keys es ->
            let edicts = es.GlobMatchChildren("**/common/edicts/*.txt")
            let inner =
                fun (node : Node) ->
                    let name = node.TagText "name"
                    match name with
                    | "" -> OK
                    | x ->
                        let x2 = "edict_" + x
                        let namedesc = x2 + "_desc"
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l x2) OK)
                            <&&>
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l namedesc) OK)
            edicts |> List.fold (fun s c -> s <&&> (inner c)) OK

    let valPolicies : LocalisationValidator =
        fun _ keys es ->
            let policies = es.GlobMatchChildren("**/common/policies/*.txt")
            let options = policies |> List.collect (fun p -> p.Childs "option")
            let inner =
                fun (node : Node) ->
                    let key = "policy_" + node.Key
                    let desc = key + "_desc"
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l key) OK)
                    <&&>
                    (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l desc) OK)
            let oinner =
                fun (node : Node) ->
                    let vals = node.Child "policy_flags" |> Option.map (fun c -> c.All |> List.choose (function |LeafValueI lv -> Some lv.Value |_ -> None )) |> Option.defaultValue []
                    let vals2 = vals |> List.map (fun v -> v.ToString() + "_name")
                    let name = node.TagText "name"
                    vals2 |> List.fold (fun s c -> s <&&> (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l c) OK)) OK
                    <&&>
                    (match name with
                    | "" -> OK
                    | x ->
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l x) OK)
                            <&&>
                        (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l (x + "_desc")) OK))
            policies |> List.fold (fun s c -> s <&&> (inner c)) OK
            <&&>
            (options |> List.fold (fun s c -> s <&&> (oinner c)) OK)

    let valSectionTemplates : LocalisationValidator =
        fun _ keys es ->
            let secs = es.GlobMatchChildren("**/common/section_templates/*.txt")
            secs |> List.fold (fun s c -> s <&&> (getLocKeys keys ["key"]  c)) OK

    let valSpeciesNames : LocalisationValidator =
        fun _ keys es ->
            let species = es.GlobMatchChildren("**/common/species_names/*.txt")
            let inner = 
                fun (node : Node) ->
                    let key = node.Key
                    let suff = ["_desc"; "_plural"; "_insult_01"; "_insult_plural_01"; "_compliment_01";"_compliment_plural_01";"_spawn";"_spawn_plural";
                                "_sound_01";"_sound_02";"_sound_03";"_sound_04";"_sound_05";"_organ";"_mouth"]
                    suff |> List.fold (fun s c -> s <&&> (keys |> List.fold (fun state (l, keys)  -> state <&&> checkLocNode node keys l (key + c)) OK)) OK
            species |> List.filter (fun s -> s.Key <> "named_lists")
                    |> List.fold (fun s c -> s <&&> (inner c)) OK

    let valStratRes : LocalisationValidator =
        fun _ keys es ->
            let res = es.GlobMatchChildren("**/common/strategic_resources/*.txt")
            res |> List.filter (fun r -> r.Key <> "time")
                |> List.fold (fun s c -> s <&&> (checkKeyAndDesc c keys)) OK
