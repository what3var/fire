#!/usr/bin/env python3
"""s3d_vk.h is made together with graphics/native/fire_gfx_vk.h - one file for both Vulkan backends (a native build has both): this runs that generator.

    python3 make_s3d_vk.py [path/to/vulkan_core.h]        (default: /usr/include/vulkan/vulkan_core.h)"""
import os
import subprocess
import sys

here = os.path.dirname(os.path.abspath(__file__))
sys.exit(subprocess.call([sys.executable, os.path.join(here, "..", "..", "..", "graphics", "native", "tools", "make_gfx_vk.py")] + sys.argv[1:]))
