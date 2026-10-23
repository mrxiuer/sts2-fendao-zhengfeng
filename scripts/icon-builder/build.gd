extends SceneTree

func _initialize() -> void:
    var args := OS.get_cmdline_user_args()
    if args.size() != 2:
        push_error("Expected source and destination icon folders")
        quit(1)
        return
    var source := args[0]
    var destination := args[1]
    DirAccess.make_dir_recursive_absolute(destination)
    var count := 0
    for filename in DirAccess.get_files_at(source):
        if filename.get_extension().to_lower() != "png":
            continue
        var image := Image.load_from_file(source.path_join(filename))
        if image == null or image.is_empty():
            push_error("Cannot load icon: " + filename)
            quit(1)
            return
        # Identical to the previous in-game display conversion. Keep original art.
        image.resize(128, 128, Image.INTERPOLATE_LANCZOS)
        image.convert(Image.FORMAT_RGBA8)
        var target := destination.path_join(filename)
        if image.save_png(target) != OK:
            push_error("Cannot save icon: " + filename)
            quit(1)
            return
        var reloaded := Image.load_from_file(target)
        reloaded.convert(Image.FORMAT_RGBA8)
        if reloaded.get_size() != Vector2i(128, 128) or reloaded.get_data() != image.get_data():
            push_error("Display pixel mismatch: " + filename)
            quit(1)
            return
        count += 1
    print("FDZF_ICON_BUILD_PASS count=" + str(count))
    quit(0)
