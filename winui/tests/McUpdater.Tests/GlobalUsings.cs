// 测试台全局 using。
//
// 为什么不靠 ImplicitUsings：xunit 的 [Fact]/[Theory]/Assert 不在
// .NET SDK 的隐式 using 列表里。而测试台刻意要与主程序保持一致的
// ImplicitUsings/Nullable 设置（否则同一份 Core 源码两边编译行为不同），
// 所以只能在这里单独补 xunit 的 using，而不能去改 ImplicitUsings 列表。
global using Xunit;
