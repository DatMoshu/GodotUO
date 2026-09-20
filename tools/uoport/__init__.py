"""uoport - shared logic for every UO_Port tool.

Every script under tools/ imports from here rather than re-deriving paths,
re-parsing config, or hardcoding knowledge about UO file formats. One place
to change when the layout or the data contract moves.

See docs/data_formats.md for the contract this package implements.
"""

from .config import Config, load_config
from .formats import DataFile, FILE_REGISTRY, required_files, uop_equivalent

__all__ = [
    "Config",
    "load_config",
    "DataFile",
    "FILE_REGISTRY",
    "required_files",
    "uop_equivalent",
]

__version__ = "0.1.0"
