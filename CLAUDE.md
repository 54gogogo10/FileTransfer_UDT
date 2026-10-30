# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

本文件为 Claude Code 在此仓库中工作时提供指引。

> **开发者指引的唯一权威来源是 [AGENTS.md](AGENTS.md)** —— 构建、测试、架构逐文件详解、线协议摘要、本地化/线程安全约定与已知坑点都在那里，且随每次功能改动同步更新。
>
> 本文件不再复制内容：历史上两份文档各自漂移，这份曾停留在早已废弃的 WinForms 架构（`MainForm.cs`、csc.exe 构建、.NET 4.5）而主程序已迁移到 WPF + SDK 构建多年。改动开发约定时**只更新 AGENTS.md**。

## 快速参考

```
# 构建（Release；产物落 release\TrFileTransfer-<版本>.exe 与 CI 用 TrFileTransfer.exe）
cd TrFileTransfer && build.bat

# 测试（自定义 TestRunner，退出码 0 = 全部通过）
cd TrFileTransfer\Tests && build.bat
TrFileTransfer.Tests.exe
```

- 运行时：.NET Framework 4.8，Windows 7 SP1+；构建需 .NET SDK 8+
- 逻辑层 20 个文件保持 C# 5 语法（进测试构建）；UI 层（WPF）不受限
- 改协议先读 [WIRE-PROTOCOL.md](WIRE-PROTOCOL.md)，并同步 AGENTS.md 的「TCP 线协议」章节

## 文档索引

| 文档 | 内容 |
|------|------|
| [AGENTS.md](AGENTS.md) | **权威开发者指引**（带目录）：构建 / 测试 / 架构 / 线协议 / 约定 / 坑点 |
| [WIRE-PROTOCOL.md](WIRE-PROTOCOL.md) | TCP / UDT / 裸 UDP 线协议完整帧布局 |
| [README.md](README.md) | 面向用户：功能介绍、快速开始、CLI 用法 |
| `docs/superpowers/` | 按日期归档的历史设计文档与实施计划（仅作背景参考） |
| `docs/security-audit-2026-09.md` | 安全审计报告（2.12/2.15 修复批次的依据） |
