namespace ${Namespace}

open System
open Eto.Forms
open Eto.Drawing
open Eto.Serialization.Xaml;

type ${EscapedIdentifier} () as this =
    inherit Form ()

    do
        XamlReader.Load (this)
