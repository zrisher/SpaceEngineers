﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VRageRender
{
    public class MyRenderMessageDrawScene : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.Draw; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.DrawScene; } }
    }

    public class MyRenderMessageUnloadData : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.StateChangeOnce; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.UnloadData; } }
    }

    public class MyRenderMessageRebuildCullingStructure : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.StateChangeOnce; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.RebuildCullingStructure; } }
    }

    public class MyRenderMessageReloadEffects : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.StateChangeOnce; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.ReloadEffects; } }
    }

    public class MyRenderMessageReloadModels : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.StateChangeOnce; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.ReloadModels; } }
    }

    public class MyRenderMessageReloadTextures : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.StateChangeOnce; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.ReloadTextures; } }
    }

    public class MyRenderMessageReloadGrass : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.StateChangeOnce; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.ReloadGrass; } }
    }

    public class MyRenderMessageUpdateEnvironmentMap : MyRenderMessageBase
    {
        public override MyRenderMessageType MessageClass { get { return MyRenderMessageType.Draw; } }
        public override MyRenderMessageEnum MessageType { get { return MyRenderMessageEnum.UpdateEnvironmentMap; } }
    }
}
