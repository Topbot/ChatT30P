angular.module('stats').controller('LinkedAccountsController', ["$rootScope", "$scope", "$location", "$filter", "$log", "dataService", function ($rootScope, $scope, $location, $filter, $log, dataService) {
    $scope.dropdownmodel = [];
    $scope.dropdownmodelcountry = [];
    $scope.dropdownsettings = {
        styleActive: true, template: '{{option}}', enableSearch: true,
        smartButtonTextConverter: function (itemText, originalItem) { return itemText; }
    };
    $scope.dropdownsettingscountry = {
        styleActive: true, enableSearch: true,
    };
    $scope.dropdowntexts = { buttonDefaultText: 'Не выбрано', checkAll: 'Выбрать все', uncheckAll: 'Убрать выбор', selectionCount: 'выбрано', dynamicButtonTextSuffix: 'выбрано', searchPlaceholder: 'Искать...' };
    $scope.dropdowntextscountry = { buttonDefaultText: 'Не выбрано', checkAll: 'Выбрать все', uncheckAll: 'Убрать выбор', selectionCount: 'выбрано', dynamicButtonTextSuffix: 'выбрано', searchPlaceholder: 'Искать...' };
    $scope.dropdownevents = { onSelectionChanged: function () { $scope.search(); } };
    $scope.dropdownoptions = [];
    $scope.topics = [
        "Компьютерные cтрелялки",//"action_game",
        "Футбол",//"association_football",
        "Бокс",//"boxing",
        "Развлечения",//"entertainment",
        "Мода",//"fashion",
        "Фильмы",//"film",
        "Еда",//"food",
        "Здоровье",//"health",
        "Увлечения",//"hobby",
        "Образование",//"knowledge",
        "Культура",//"lifestyle_(sociology)",
        "Рукопашный бой",//"mixed_martial_arts",
        "Музыка",//"music",
        "Домашние животные",//"pet",
        "Фитнес тренировки",//"physical_fitness",
        "Политика",//"politics",
        "Рок-музыка",//"rock_music",
        "Ролевые игры",//"role-playing_video_game",
        "Общество",//"society",
        "Спорт",//"sport",
        "Стратегические игры",//"strategy_video_game",
        "Технологии",//"technology",
        "Туризм",//"tourism",
        "Машины",//"vehicle",
        "Компьютерные игры",//"video_game_culture"
        "Спортивные игры",//sports_game
        "Бизнес",//"business",
        "Юмор",//"humour",
        "Искусство",//"performing_arts",
        "Религия",//"religion",
        "Поп-музыка",//"pop_music",
        "Компьютерные симуляторы",//"simulation_video_game",
        "Игры бродилки", //"action-adventure_game",
        "Вооружённые силы", //"military",
        "Баскетбол",//    "basketball",
        "Компьютерные гонки",//    "racing_video_game",
        "Хип-хоп музыка",//"hip_hop_music",
        "Электронная музыка",//"electronic_music",
        "Мотоциклы",//"motorsport",
        "Логические игры",//puzzle_video_game
        "Музыкальные игры",//music_video_game
        "ТВ передача",//"television_program",
        "Азиатская музыка",//"music_of_asia",
        "Классическая музыка",//"classical_music",
        "Хоккей",//"ice_hockey",
        "Культуризм",//"physical_attractiveness"
        "Гольф",//"golf",
        "Казуальные игры",//"casual_game",
        "Христианская музыка",//"christian_music",
        "Волейбол",//"volleyball",
        "Джаз",//"jazz",
        "Теннис",//"tennis",
        "Реслинг",//"professional_wrestling"
        "Регги",//reggae
        "Инди-музыка",//"independent_music",
        "Латиноамериканская музыка",//"music_of_latin_america",
        "Ритм-энд-блюз",//"rhythm_and_blues",
        "Соул-музыка"//"soul_music"
    ];

    $scope.items = [];
    $scope.item = {};
    $scope.id = ($location.search()).id;
    $scope.filter = ($location.search()).fltr;
    $scope.datatype = 1;
    $scope.filterfield = 'G';
    $scope.sliderConfig = {
        min: 0,
        max: 100,
        step: 100,
        _userMin: 0,
        _userMax: 100,
        userMin: function (newValue) {
            if (arguments.length) {
                var changed = $scope.sliderConfig._userMin == newValue;
                $scope.sliderConfig._userMin = newValue;
                if (changed && angular.isDefined($scope.search)) {
                    $scope.search();
                }
            }
            return $scope.sliderConfig._userMin;
        },
        userMax: function (newValue) {
            if (arguments.length) {
                var changed = $scope.sliderConfig._userMax == newValue;
                $scope.sliderConfig._userMax = newValue;
                if (changed && angular.isDefined($scope.search)) {
                    $scope.search();
                }
            }
            return $scope.sliderConfig._userMax;
        },
        disabled: true
    };

    $scope.addNew = function () {
        $("#modal-add-new").modal();
        $scope.focusInput = true;
    }

    $scope.addBlogger = function () {
        if (!$('#form-add-new').valid()) {
            return false;
        }
        spinOn();
        dataService.addItem("/api/youtube", { id: $('#txtBloglink').val() }, function (data) {
            toastr.success($rootScope.lbl.completed);
            spinOff();
            alert($rootScope.lbl.newAdded);
            //$("#modal-add-new").modal('hide');
        }, function () {
            toastr.error($rootScope.lbl.failed);
            spinOff();
        });
    }

    $scope.load = function () {
        //только если не использовали категорию
        if ($scope.dropdownmodel == undefined || $scope.dropdownmodel.length == 0) {
            if ($scope.dropdownmodelcountry == undefined || $scope.dropdownmodelcountry.length == 0) {
                var p = {
                    take: 0, skip: 0, filter: this.query, order: "",
                    type: this.datatype,
                    min: this.sliderConfig.disabled ? 0 : this.sliderConfig.userMin(),
                    max: this.sliderConfig.disabled ? 0 : this.sliderConfig.userMax()
                };
                if (p.filter !== undefined) {
                    p.take = 100;
                }
                if (dataService.checkAuth()) {
                    beforeDataLoad();
                    dataService.getItems('/api/youtube', p, function (result) {
                        onDataLoad($scope, result.data);
                        gridInit($scope, $filter);
                        if ($scope.dropdownoptions.length == 0) {
                            for (var i = 0; i < countries.length; i++) {
                                var byC = result.data.filter(function (item) {
                                    return item["c"] == countries[i].id;
                                });
                                if (byC.length > 0) {
                                    $scope.dropdownoptions.push(countries[i]);
                                }
                            }
                        }
                        for (var i = 0; i < result.data.length; i++) {
                            if (result.data[i]["T"] != null && result.data[i]["T"] != '') {
                                var topics = result.data[i]["T"].split(",");
                                var ts = "";
                                for (var j = 0; j < topics.length; j++) {
                                    ts += $scope.topics[parseInt(topics[j])] + ",";
                                }
                                result.data[i]["T"] = ts.slice(0, -1);
                            }
                        }
                        $scope.topics.sort();
                        rowSpinOff($scope.filteredItems);
                    }, function (data) {
                        toastr.error($rootScope.lbl.errorGettingTags);
                    });
                }
            }
        }
    }

    $scope.load();

    $scope.showPage = function (n, current, total) {
        if (!current) {
            current = 0;
        }
        if (n >= current - 10 && n <= current + 10) {
            return true;
        }
        if (n + 1 === total) {
            return true;
        }
        return false;
    }

    $scope.save = function () {
        if ($scope.tag) {
            dataService.updateItem("/api/youtube", { item: $scope.item }, function (data) {
               toastr.success($rootScope.lbl.commentUpdated);
               $scope.load();
           }, function () { toastr.error($rootScope.lbl.updateFailed); });
        }
        $("#modal-add-item").modal('hide');
    }

    $scope.processChecked = function (action) {
        var i = $scope.items.length;
        var checked = [];
        while (i--) {
            var item = $scope.items[i];
            if (item.IsChecked === true) {
                checked.push(item);
            }
        }
        if (checked.length < 1) {
            return false;
        }
        if (!UserVars.IsPaid) {
            alert($rootScope.lbl.shouldPay);
            return false;
        }

        if (action === "delete") {
            spinOn();
            dataService.processChecked("/api/youtube/processchecked/delete", checked, function (data) {
                var i2 = checked.length;
                while (i2--) {
                    var index = $scope.items.indexOf(checked[i2]);
                    $scope.items.splice(index, 1);
                }
                $scope.search();
                toastr.success($rootScope.lbl.completed);
                if ($('#chkAll')) {
                    $('#chkAll').prop('checked', false);
                }
                spinOff();
            }, function () {
                toastr.error($rootScope.lbl.failed);
                spinOff();
            });
        }
    }

    $scope.forward = function () {
        $scope.save();
        if ($scope.items.length > $scope.currentIndex + 1) {
            $scope.rowClick($scope.items[$scope.currentIndex + 1]);
        }
    }

    $scope.getCountry = function (id) {
        var item = countries.filter(function (item) {
            return (item.id === id);
        });
        if (item != null && item.length > 0) {
            return item[0].label
        } else {
            return null;
        }
    }
}]);