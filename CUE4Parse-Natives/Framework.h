#pragma once
#include <acl/core/ansi_allocator.h>
#include <acl/core/compressed_tracks.h>
#include <acl/decompression/decompress.h>
#include "Structs.h"

#define DLLEXPORT extern "C" __declspec(dllexport)
acl::ansi_allocator ACLAllocatorImpl;
using DecompContextDefault = acl::decompression_context<acl::decompression_settings>;