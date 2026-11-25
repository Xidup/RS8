// 等价于 MSBuild 的 <ImplicitUsings>enable</ImplicitUsings> + UseWindowsForms 的隐式 using
// 裸 Roslyn 编译时不会自动生成，需要手写。
global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Runtime.InteropServices;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Windows.Forms;
