# Third-Party Notices

This product includes code adapted from third-party open source projects. The original copyright notices and license terms are reproduced below.

---

## mcp-servers-for-revit

Source: https://github.com/mcp-servers-for-revit/mcp-servers-for-revit

ZeManage's `BIManage/AI/Terminal/Tools/` and `BIManage/AI/Terminal/Execution/` directories contain code adapted from the Sparx fork of revit-mcp.

### MIT License

```
MIT License

Copyright (c) 2026 sparx-fire, mcp-servers-for-revit
Copyright (c) 2024 revit-mcp original authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## Modifications

The code from mcp-servers-for-revit has been modified by ZestineTech to:

1. Replace external `RevitMCPSDK` NuGet dependency with inline base classes
2. Remove TCP socket layer (`SocketService`) — replaced with in-process MCP server
3. Add Roslyn AST static analyzer for code execution safety
4. Wire tool execution into ZeManage's audit logging system
5. Adapt namespace from `RevitMCPCommandSet.*` to `BIManage.AI.Terminal.*`
