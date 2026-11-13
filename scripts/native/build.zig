const std = @import("std");

// Upstream's default install also compiles an unused static terminal library.
// Its dependency interface exposes the shared artifact without modifying pinned source.
pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const ghostty = b.dependency("ghostty", .{
        .target = target,
        .optimize = optimize,
        .@"emit-lib-vt" = true,
    });
    b.installArtifact(ghostty.artifact("ghostty-vt"));
}
