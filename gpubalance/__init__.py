"""Workload balancing across two GPUs, on Linux and Windows."""

import logging

__version__ = "0.4.0"

# Library use stays quiet until the CLI (or the caller) sets up logging.
logging.getLogger(__name__).addHandler(logging.NullHandler())
