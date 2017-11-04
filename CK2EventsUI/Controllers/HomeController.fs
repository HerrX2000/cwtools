﻿namespace CK2_Events.Controllers

open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open Microsoft.AspNetCore.Mvc
open CK2_Events.Application
open FParsec


type HomeController () =
    inherit Controller()

    //static let ck2 = Events.parseTen

    member this.Index () =
        eprintfn "Test"
        let ck2 = Events.parseTen
        this.View(ck2)  
        

    member this.Error () =
        this.View();
